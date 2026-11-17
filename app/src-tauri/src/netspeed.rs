//! 实时网速采样。
//!
//! # 为什么不用第三方库
//!
//! 只需要读两个 64 位计数器（收/发字节数），`GetIfTable2` 是系统自带的，
//! 加个 `windows-sys` feature 就行，比引一个统计库划算得多。
//!
//! # 口径
//!
//! 统计的是**网卡层的真实字节增量**，不是链路利用率百分比。
//! 所以几 KB/s 的后台心跳在这里是能看见的，而在任务管理器的「网络」列里
//! （那是占带宽的百分比）永远是 0%。两个数字对不上是正常的，不是谁错了。
//!
//! # 过滤规则
//!
//! 1. 只要 `OperStatus == Up` 的网卡
//! 2. 跳过回环（type 24）和隧道（type 131）—— 先单独统计
//! 3. 跳过描述里带虚拟化关键词的网卡（VMware / Hyper-V / VirtualBox / TAP / Wintun …）
//!    这类网卡会把同一份流量再算一遍
//! 4. **隧道优先**：只要有隧道网卡在线且有流量，就只统计隧道，不再统计物理网卡。
//!    否则 VPN 开着的时候同一份流量会被算两次（物理网卡一次、隧道一次）
//!
//! 这套规则是照着 `reference/netspeedball/NetSpeedBall.cs` 的口径复刻的，
//! 那边踩过的坑这里不再踩一遍。

use std::sync::Mutex;
use std::sync::OnceLock;
use std::thread;
use std::time::{Duration, Instant};

use serde::Serialize;

/// 采样间隔。250ms 足够跟上「火箭转速」那种视觉反馈，又不会明显吃 CPU。
const SAMPLE_INTERVAL: Duration = Duration::from_millis(250);

/// 指数平滑系数。越小越平滑、越迟钝。
///
/// 0.35 是折中：单次采样的毛刺（一次大包突发）会被压掉大半，
/// 但流量真的停下来时大约 0.7 秒内就能看到归零，不会拖泥带水。
const SMOOTHING: f64 = 0.35;

/// 低于这个速率按 0 处理（单位：字节/秒）。
///
/// 系统心跳、NTP 校时、推送保活这些噪声常年挂在 1~5 KB/s，
/// 不压掉的话球上永远是「1.2K」而不是「0」。
const NOISE_FLOOR: f64 = 8.0 * 1024.0;

/// 一次采样的结果，直接序列化给前端。
#[derive(Clone, Copy, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NetSpeedSample {
    /// 下行速率，字节/秒（已平滑）
    pub down: f64,
    /// 上行速率，字节/秒（已平滑）
    pub up: f64,
    /// 本次开机以来的下行总字节数
    pub down_total: u64,
    /// 本次开机以来的上行总字节数
    pub up_total: u64,
    /// 是否至少找到一块可统计的网卡。
    /// false 时前端应该显示「--」而不是「0」，两者含义不同：
    /// 一个是「没网卡」，一个是「有网卡但没流量」。
    pub available: bool,
}

/// 全局只存最新一次采样。前端按需来取，不推送。
///
/// 为什么用轮询而不是 Tauri 事件推送：
/// 球窗口本来就要以固定频率重绘（数字在变），轮询天然对齐重绘节奏；
/// 而事件推送要处理监听器泄漏、窗口销毁后的回调，复杂度换不来收益。
static LATEST: OnceLock<Mutex<NetSpeedSample>> = OnceLock::new();

fn cell() -> &'static Mutex<NetSpeedSample> {
    LATEST.get_or_init(|| Mutex::new(NetSpeedSample::default()))
}

/// 取最新采样。前端每帧调它。
pub fn latest() -> NetSpeedSample {
    *cell().lock().unwrap_or_else(|e| e.into_inner())
}

/// 启动后台采样线程。由 `lib.rs` 的 setup 调用，进程内只会启动一次。
pub fn start() {
    // 先同步采一次基准值，否则第一次调用会拿到 0 并且要等一个间隔才有数
    let _ = read_counters();
    thread::spawn(|| {
        let mut prev = match read_counters() {
            Some(v) => v,
            None => Counters::default(),
        };
        let mut prev_at = Instant::now();

        let mut smooth_down = 0.0f64;
        let mut smooth_up = 0.0f64;
        let mut primed = false;

        loop {
            thread::sleep(SAMPLE_INTERVAL);

            let now = Instant::now();
            let elapsed = now.duration_since(prev_at).as_secs_f64();
            // 时钟被改动或线程被挂起过久时 elapsed 可能异常，这一轮直接跳过，
            // 否则会算出一个天文数字的速率
            if elapsed < 0.05 || elapsed > 5.0 {
                prev_at = now;
                prev = read_counters().unwrap_or(prev);
                continue;
            }

            let Some(cur) = read_counters() else {
                prev_at = now;
                continue;
            };

            // 计数器回绕或网卡被拔掉重插时会变小，这时不能算出差值
            let d_down = cur.down.saturating_sub(prev.down) as f64 / elapsed;
            let d_up = cur.up.saturating_sub(prev.up) as f64 / elapsed;

            let (mut down, mut up) = if primed {
                (
                    smooth_down + (d_down - smooth_down) * SMOOTHING,
                    smooth_up + (d_up - smooth_up) * SMOOTHING,
                )
            } else {
                // 第一轮没有历史值，直接采用，避免从 0 慢慢爬上去
                primed = true;
                (d_down, d_up)
            };

            if down < NOISE_FLOOR {
                down = 0.0;
            }
            if up < NOISE_FLOOR {
                up = 0.0;
            }

            smooth_down = down;
            smooth_up = up;

            {
                let mut slot = cell().lock().unwrap_or_else(|e| e.into_inner());
                *slot = NetSpeedSample {
                    down,
                    up,
                    down_total: cur.down,
                    up_total: cur.up,
                    available: cur.available,
                };
            }

            prev = cur;
            prev_at = now;
        }
    });
}

/// 累计字节数快照。
#[derive(Clone, Copy, Default)]
struct Counters {
    down: u64,
    up: u64,
    available: bool,
}

/// 网卡的一行原始信息，从系统表里读出来后立刻转成纯数据，
/// 免得把 `MIB_IF_ROW2` 这种大结构体搬来搬去。
#[derive(Clone, Copy)]
struct AdapterRow {
    is_tunnel: bool,
    virtual_like: bool,
    down: u64,
    up: u64,
}

/// 读一次系统网卡计数器并汇总。
///
/// 返回 `None` 表示这次读失败了（系统调用失败），调用方应该沿用上一轮的值。
#[cfg(windows)]
fn read_counters() -> Option<Counters> {
    use windows_sys::Win32::NetworkManagement::IpHelper::{
        FreeMibTable, GetIfTable2, MIB_IF_TABLE2,
    };

    // IF_OPER_STATUS_UP
    const OPER_UP: i32 = 1;
    // IF_TYPE_SOFTWARE_LOOPBACK / IF_TYPE_TUNNEL
    const TYPE_LOOPBACK: u32 = 24;
    const TYPE_TUNNEL: u32 = 131;

    let mut table: *mut MIB_IF_TABLE2 = std::ptr::null_mut();
    // SAFETY: GetIfTable2 成功时会分配一张表并由我们负责 FreeMibTable。
    // 这里只在返回 0（NO_ERROR）时使用它，且下面一定释放。
    let rc = unsafe { GetIfTable2(&mut table) };
    if rc != 0 || table.is_null() {
        return None;
    }

    let mut rows: Vec<AdapterRow> = Vec::new();
    // SAFETY: table 非空且由系统填充；NumEntries 是有效行数。
    unsafe {
        let count = (*table).NumEntries as usize;
        let first = (*table).Table.as_ptr();
        for i in 0..count {
            let row = &*first.add(i);

            if row.OperStatus != OPER_UP {
                continue;
            }
            if row.Type == TYPE_LOOPBACK {
                continue;
            }

            let alias = utf16_field(&row.Alias);
            let desc = utf16_field(&row.Description);
            let haystack = format!("{alias} {desc}").to_ascii_lowercase();

            rows.push(AdapterRow {
                is_tunnel: row.Type == TYPE_TUNNEL,
                virtual_like: looks_virtual(&haystack),
                down: row.InOctets,
                up: row.OutOctets,
            });
        }
    }
    // SAFETY: table 由 GetIfTable2 分配，必须交还系统
    unsafe { FreeMibTable(table as *const core::ffi::c_void) };

    if rows.is_empty() {
        return Some(Counters {
            down: 0,
            up: 0,
            available: false,
        });
    }

    let tunnel_down: u64 = rows.iter().filter(|r| r.is_tunnel).map(|r| r.down).sum();
    let tunnel_up: u64 = rows.iter().filter(|r| r.is_tunnel).map(|r| r.up).sum();

    // 隧道在线且有流量 → 只认隧道，避免和物理网卡重复计数
    if tunnel_down > 0 || tunnel_up > 0 {
        return Some(Counters {
            down: tunnel_down,
            up: tunnel_up,
            available: true,
        });
    }

    let mut down = 0u64;
    let mut up = 0u64;
    let mut any = false;
    for r in rows.iter().filter(|r| !r.is_tunnel && !r.virtual_like) {
        down += r.down;
        up += r.up;
        any = true;
    }

    // 过滤太狠（比如用户网卡描述里有奇怪字样）导致一个都不剩时，
    // 退回「所有非隧道网卡」，宁可重复计数也不要显示 0
    if !any {
        for r in rows.iter().filter(|r| !r.is_tunnel) {
            down += r.down;
            up += r.up;
            any = true;
        }
    }

    Some(Counters {
        down,
        up,
        available: any,
    })
}

#[cfg(not(windows))]
fn read_counters() -> Option<Counters> {
    // 浮光只发 Windows 版，这里只是为了让 cargo check 在别的平台上不至于报错
    None
}

/// 从定长 UTF-16 数组里取出字符串，遇到 NUL 截断。
///
/// 这些字段是 `[u16; 257]` 这种定长数组，末尾可能没有 NUL，
/// 所以用 `from_utf16_lossy` 而不是 `from_utf16`（后者遇到孤立代理项会失败，
/// 而网卡描述是驱动写的，不能保证一定合法）。
#[cfg(windows)]
fn utf16_field(buf: &[u16]) -> String {
    let end = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
    String::from_utf16_lossy(&buf[..end])
}

/// 判断网卡描述像不像虚拟网卡。
///
/// 这是启发式判断，不是系统给的权威标记 —— 系统的 `PhysicalMediumType`
/// 在很多虚拟网卡上照样报「未知」，分不出来。关键词表是照着
/// 常见虚拟化产品整理的，遇到没覆盖到的就把描述里那个词加进来。
fn looks_virtual(lowercased: &str) -> bool {
    const KEYWORDS: [&str; 12] = [
        "vmware",
        "hyper-v",
        "virtualbox",
        "vbox",
        "tap-windows",
        "wintun",
        "loopback",
        "npcap",
        "openvpn",
        "wireguard",
        "zerotier",
        "tailscale",
    ];
    KEYWORDS.iter().any(|k| lowercased.contains(k))
}
