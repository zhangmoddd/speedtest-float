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
//! 4. **隧道优先，但按"这一拍的增量"比**：隧道和物理网卡各算一组累计值，
//!    每一拍看谁真的在涨。隧道增量不小于物理增量的一半，才只统计隧道；
//!    否则只统计物理网卡。全隧道模式下两者接近（隧道略小，少了 VPN 封装开销），
//!    所以选隧道；分流 VPN、或者 Tailscale 这类常驻但没在传数据的隧道，
//!    物理增量远大于隧道增量，就选物理。
//!
//!    以前这里是按**开机以来的累计值**判断的（隧道累计 > 0 就只认隧道）：
//!    常驻隧道网卡开机后传过一点数据就永远 > 0，之后真实走 Wi-Fi 的流量会被整段漏掉，
//!    球上一直显示接近 0。
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
        let mut prev = read_counters().unwrap_or_default();
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

            // 这一拍统计哪一组（隧道 / 其余），以及那一组的字节增量
            let (down_bytes, up_bytes, used_tunnel) = pick_group(&prev, &cur);
            let d_down = down_bytes as f64 / elapsed;
            let d_up = up_bytes as f64 / elapsed;

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
                    down_total: if used_tunnel { cur.tunnel_down } else { cur.other_down },
                    up_total: if used_tunnel { cur.tunnel_up } else { cur.other_up },
                    available: cur.available,
                };
            }

            prev = cur;
            prev_at = now;
        }
    });
}

/// 两组网卡的累计字节数快照（开机以来）。
///
/// 隧道一组、其余可统计的网卡一组；选哪组是每一拍按增量决定的，见 [`pick_group`]。
#[derive(Clone, Copy, Default, PartialEq, Debug)]
struct Counters {
    tunnel_down: u64,
    tunnel_up: u64,
    other_down: u64,
    other_up: u64,
    available: bool,
}

/// 决定这一拍统计哪一组：返回 `(下行增量, 上行增量, 是否选了隧道)`。
///
/// 按**这一拍的增量**比，不按开机以来的累计值：
/// - 常驻隧道（Tailscale 这类）开机后传过一点数据，累计值永远 > 0，但此刻没在涨；
///   真实流量走的是 Wi-Fi —— 必须选物理。
/// - 全隧道 VPN（出口节点）：隧道增量 ≈ 物理增量（隧道略小），两者都在涨 —— 选隧道，
///   它是应用层的真实字节数，而且不会把同一份流量算两遍。
/// - 分流 VPN：物理增量远大于隧道增量 —— 选物理（它的数字里已经包含了 VPN 的封装流量）。
///
/// 计数器回绕或网卡重插会变小，`saturating_sub` 保证不会算出负数/天文数字。
fn pick_group(prev: &Counters, cur: &Counters) -> (u64, u64, bool) {
    let tunnel_down = cur.tunnel_down.saturating_sub(prev.tunnel_down);
    let tunnel_up = cur.tunnel_up.saturating_sub(prev.tunnel_up);
    let other_down = cur.other_down.saturating_sub(prev.other_down);
    let other_up = cur.other_up.saturating_sub(prev.other_up);

    let tunnel = tunnel_down.saturating_add(tunnel_up);
    let other = other_down.saturating_add(other_up);
    if tunnel > 0 && tunnel.saturating_mul(2) >= other {
        (tunnel_down, tunnel_up, true)
    } else {
        (other_down, other_up, false)
    }
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
        return Some(Counters::default());
    }

    let mut counters = Counters::default();
    for r in rows.iter().filter(|r| r.is_tunnel) {
        counters.tunnel_down = counters.tunnel_down.saturating_add(r.down);
        counters.tunnel_up = counters.tunnel_up.saturating_add(r.up);
    }
    for r in rows.iter().filter(|r| !r.is_tunnel && !r.virtual_like) {
        counters.other_down = counters.other_down.saturating_add(r.down);
        counters.other_up = counters.other_up.saturating_add(r.up);
        counters.available = true;
    }

    // 过滤太狠（比如用户网卡描述里有奇怪字样）导致一块普通网卡都不剩时，
    // 退回「所有非隧道网卡」，宁可重复计数也不要显示 0
    if !counters.available {
        for r in rows.iter().filter(|r| !r.is_tunnel) {
            counters.other_down = counters.other_down.saturating_add(r.down);
            counters.other_up = counters.other_up.saturating_add(r.up);
            counters.available = true;
        }
    }
    // 只有隧道、一块普通网卡都没有的机器也算"有网"
    counters.available = counters.available || counters.tunnel_down > 0 || counters.tunnel_up > 0;

    Some(counters)
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

#[cfg(test)]
mod tests {
    use super::*;

    fn counters(tunnel: (u64, u64), other: (u64, u64)) -> Counters {
        Counters {
            tunnel_down: tunnel.0,
            tunnel_up: tunnel.1,
            other_down: other.0,
            other_up: other.1,
            available: true,
        }
    }

    #[test]
    fn idle_tunnel_with_history_does_not_steal_from_physical() {
        // Tailscale 这类常驻隧道：开机后传过 1MB，之后没动；真实流量走 Wi-Fi
        let prev = counters((1_000_000, 500_000), (10, 10));
        let cur = counters((1_000_000, 500_000), (10 + 3_000, 10 + 700));
        assert_eq!(pick_group(&prev, &cur), (3_000, 700, false));
    }

    #[test]
    fn full_tunnel_prefers_tunnel_counters() {
        // 全隧道 VPN：两组都在涨，隧道略小（少了封装开销）→ 选隧道，只算一遍
        let prev = counters((100, 100), (100, 100));
        let cur = counters((100 + 1_000, 100 + 200), (100 + 1_050, 100 + 210));
        assert_eq!(pick_group(&prev, &cur), (1_000, 200, true));
    }

    #[test]
    fn split_tunnel_falls_back_to_physical() {
        // 分流 VPN：隧道只涨一点点，物理涨很多 → 选物理
        let prev = counters((100, 100), (100, 100));
        let cur = counters((100 + 100, 100 + 20), (100 + 5_000, 100 + 900));
        assert_eq!(pick_group(&prev, &cur), (5_000, 900, false));
    }

    #[test]
    fn tunnel_only_machine_counts_tunnel() {
        let prev = counters((100, 100), (0, 0));
        let cur = counters((100 + 400, 100 + 50), (0, 0));
        assert_eq!(pick_group(&prev, &cur), (400, 50, true));
    }

    #[test]
    fn counter_reset_never_underflows() {
        // 网卡重插后计数器归零：不能算出天文数字
        let prev = counters((0, 0), (9_000_000, 9_000_000));
        let cur = counters((0, 0), (120, 80));
        assert_eq!(pick_group(&prev, &cur), (0, 0, false));
    }

    #[test]
    fn nothing_moving_is_zero() {
        let same = counters((7, 7), (9, 9));
        assert_eq!(pick_group(&same, &same), (0, 0, false));
    }

    #[test]
    fn virtual_keyword_matches_known_products_only() {
        assert!(looks_virtual("vethernet (default switch) hyper-v virtual ethernet adapter"));
        assert!(looks_virtual("wireguard tunnel"));
        assert!(looks_virtual("vmware virtual ethernet adapter for vmnet8"));
        assert!(!looks_virtual("intel(r) wi-fi 6 ax201 160mhz"));
        assert!(!looks_virtual("realtek pcie gbe family controller"));
    }
}
