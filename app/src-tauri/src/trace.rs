//! 「足迹」：记住一个文件/文件夹的**身份**，它改了名、搬了家之后还能被找回来。
//!
//! # 为什么路径靠不住
//!
//! 链接现在把"目标在哪"存成一个字符串。而路径只是**当前位置的地址**，
//! 不是**这个东西本身**。文件夹一改名，整条路径就作废：
//!
//! ```text
//! 原来：D:\Project\workbuddy\2026-09-14-20-48-18\清内存-双击这个.bat
//! 后来：D:\Project\workbuddy\一键清内存工具\清内存-双击这个.bat
//! ```
//!
//! 文件一个字节都没动，只是被拖进了改名后的文件夹。可对链接来说，
//! 它"整个消失了" —— 原来的 `find_same_name` 从**原父目录**往下找，
//! 而那个父目录本身已经不在了，于是它连搜都没开始搜。
//!
//! # 什么是不随改名变化的东西
//!
//! NTFS 给卷上每个文件/文件夹分配一个**文件索引号**（File ID），
//! 卷本身有一个**序列号**（Volume Serial Number）。这一对 `(卷号, 索引号)`
//! 在文件的整个生命周期里**不变**：
//!
//! - 重命名 → 不变
//! - 移动到**同一个卷**的任何位置 → 不变
//! - 内容被改写、大小变了 → 不变
//!
//! 它只在三件事上会变：文件被删除后重建、跨卷移动（那是"复制 + 删除"，
//! 在新卷上是个全新的文件）、以及文件系统本身不支持（FAT32/exFAT 的 U 盘）。
//!
//! 所以：**记下这一对号，就等于记下了这个文件的身份证**。名字和路径都可以变，
//! 身份证不会。这就是"顺着因果找回去"的那条因果链 ——
//! 不是猜"它可能叫什么名字、大概在哪"，而是**按身份核对**。
//!
//! # 一个必须说清的限度
//!
//! **找到的前提是它还在这个卷上。** 文件被删除、被移到另一个盘、
//! 或者所在的分区被格式化过，身份号就跟着没了 —— 那时候任何程序都找不回来
//! （能不能恢复是数据恢复工具的活，不是启动器的活）。
//! 这种情况会退回"同名文件"和"同大小同时间"两条弱线索，并在界面上
//! 明确标成「可能」而不是「就是它」。
//!
//! # 为什么不走 USN 日志 / `OpenFileById`
//!
//! - **USN 变更日志**能直接告诉我们"这个 ID 被改名成什么、挂到哪个目录下了"，
//!   是最快的路。但读它需要管理员权限，而浮光是普通权限常驻的 ——
//!   一条"有时候能用"的路会让整个功能的表现随权限漂移，很难解释也很难测。
//! - **`OpenFileById` + `GetFinalPathNameByHandleW`** 也能直接由 ID 换回路径，
//!   同样受特权（`SE_BACKUP_NAME`）和 ACL 影响。
//!
//! 所以这里走**枚举目录项**这条路：只要对该目录有读权限就能走，
//! 行为稳定、可测试，代价是要扫。用预算（条目数 + 时间）把代价框住。
#![cfg(windows)]

use std::collections::VecDeque;
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

use windows_sys::Win32::Foundation::{CloseHandle, FILETIME, HANDLE, INVALID_HANDLE_VALUE};
use windows_sys::Win32::Storage::FileSystem::{
    CreateFileW, GetFileInformationByHandle, GetFileInformationByHandleEx, BY_HANDLE_FILE_INFORMATION,
    FILE_ATTRIBUTE_DIRECTORY, FILE_FLAG_BACKUP_SEMANTICS, FILE_ID_BOTH_DIR_INFO, FILE_LIST_DIRECTORY,
    FILE_READ_ATTRIBUTES, FILE_SHARE_DELETE, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING,
};

/// 一次搜索最多看多少个目录项。
///
/// 和 `launcher::find_same_name` 的 [`crate::launcher`] 那套预算同一个思路：
/// 没有上限的搜索就是拿用户的磁盘做全盘扫描。这里给得比同名搜索宽
/// （20 万 vs 4000），因为**这里的命中是有确定性的** —— 值得多找一会儿。
const MAX_ENTRIES: usize = 200_000;
/// 一次搜索最多花多久。到点带着已有结果返回。
const BUDGET: Duration = Duration::from_millis(5000);
/// 最多返回几个候选。
const MAX_HITS: usize = 8;
/// 最多往下走几层。
const MAX_DEPTH: usize = 8;

/// 一个目录项的身份信息（枚举时顺手拿到的）。
struct EntryInfo {
    name: String,
    file_id: i64,
    bytes: u64,
    modified_ms: Option<i64>,
    is_dir: bool,
    attributes: u32,
}

/// 一个文件/文件夹的**身份**。序列化进 `links.json`，跟着链接一起存。
///
/// # 为什么连 `bytes` / `modifiedMs` 一起存
///
/// 光有 `(卷号, 索引号)` 只能证明"是它"，但**证明不了它现在还是原来那个东西**：
/// 索引号在文件被原地改写时不变。大小和修改时间能补上这一层 ——
/// 摆在一起给用户看，他才能判断"这是我要的那个，还是被别的程序覆盖过了"。
///
/// 这两项还有第二个用处：跨卷移动（复制 + 删除）之后索引号全变，
/// 但 Windows 复制会**保留修改时间**，于是"大小相同 + 修改时间几乎相同"
/// 就成了唯一还活着的线索。
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FileTrace {
    /// 文件所在卷的序列号。
    pub volume_serial: u32,
    /// 文件在卷上的索引号（NTFS 的 MFT 记录号 + 序列号，合成 64 位）。
    pub file_id: i64,
    /// 记录时的字节数。目录是 0。
    #[serde(default)]
    pub bytes: u64,
    /// 记录时的最后修改时刻（Unix 毫秒）。
    #[serde(default)]
    pub modified_ms: Option<i64>,
    /// 记录时的文件名（含扩展名）。只用于在界面上把"原来是哪个"说清楚。
    #[serde(default)]
    pub name: String,
}

/// 一个"找回来了"的候选。
#[derive(Debug, Clone, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TraceHit {
    pub path: String,
    pub bytes: u64,
    pub modified_ms: Option<i64>,
    /// 凭什么认为它是原来那个。
    pub how: TraceMatch,
}

/// 命中的把握程度。前端按这个决定措辞 —— 措辞错了比找不着更糟。
#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub enum TraceMatch {
    /// 卷号 + 文件索引号都对上了：**就是它**，改名或搬家而已。
    SameFile,
    /// 大小相同、修改时间几乎相同：很可能是它，但可能是副本。
    SameContent,
}

/// 把 Rust 字符串转成以 NUL 结尾的宽字符。
fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

/// `FILETIME` → Unix 毫秒。
fn filetime_to_ms(ft: FILETIME) -> Option<i64> {
    let ticks = ((ft.dwHighDateTime as u64) << 32) | ft.dwLowDateTime as u64;
    ticks_to_ms(ticks as i64)
}

/// 100 纳秒计数 → Unix 毫秒。
///
/// 两个结构用的表示不一样：`BY_HANDLE_FILE_INFORMATION` 给的是
/// [`FILETIME`]（高低两段 `u32`），`FILE_ID_BOTH_DIR_INFO` 给的是
/// 同一个值的 `i64` 形式。算的是同一件事，所以归到一个函数里，
/// 免得"1601 到 1970 差多少秒"这个魔数写两遍、改一处漏一处。
fn ticks_to_ms(ticks: i64) -> Option<i64> {
    if ticks <= 0 {
        return None;
    }
    // FILETIME 是"1601-01-01 起的 100 纳秒数"，Unix 纪元差 11644473600 秒
    Some(ticks / 10_000 - 11_644_473_600_000)
}

/// 读一个路径的身份。路径不存在、没权限、在断开的网络盘上 → `None`。
///
/// # 为什么要走 `CreateFileW` 而不是 `std::fs::metadata`
///
/// `std::fs::metadata` 的返回值里**没有卷序列号，也没有文件索引号** ——
/// 这两样只在 Win32 的 `BY_HANDLE_FILE_INFORMATION` 里。
/// 一次 `CreateFileW` + `GetFileInformationByHandle` 就把属性、大小、
/// 修改时间、卷号、索引号全拿到了，并不比 `stat` 贵。
///
/// # `FILE_FLAG_BACKUP_SEMANTICS` 不能省
///
/// 少了它，`CreateFileW` 打不开**目录**（返回 `ERROR_ACCESS_DENIED`）——
/// 而文件夹链接（这个功能里占了一半）恰恰需要它。
pub fn trace_of(path: &str) -> Option<FileTrace> {
    let p = Path::new(path);
    if path.trim().is_empty() {
        return None;
    }

    let w = wide(path);
    // SAFETY: `w` 以 NUL 结尾且在本函数内一直有效；句柄用后立即关闭。
    let handle = unsafe {
        CreateFileW(
            w.as_ptr(),
            FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            std::ptr::null(),
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,
            std::ptr::null_mut(),
        )
    };
    if handle == INVALID_HANDLE_VALUE || handle.is_null() {
        return None;
    }

    let mut info: BY_HANDLE_FILE_INFORMATION = unsafe { std::mem::zeroed() };
    // SAFETY: 句柄有效；`info` 是这块内存的唯一写者。
    let ok = unsafe { GetFileInformationByHandle(handle, &mut info) };
    // SAFETY: 句柄来自上面那次成功的 `CreateFileW`，且只关一次。
    unsafe { CloseHandle(handle) };
    if ok == 0 {
        return None;
    }

    let file_id = ((info.nFileIndexHigh as u64) << 32 | info.nFileIndexLow as u64) as i64;
    Some(FileTrace {
        volume_serial: info.dwVolumeSerialNumber,
        file_id,
        bytes: if info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY != 0 {
            0
        } else {
            (info.nFileSizeHigh as u64) << 32 | info.nFileSizeLow as u64
        },
        modified_ms: filetime_to_ms(info.ftLastWriteTime),
        name: p
            .file_name()
            .and_then(|n| n.to_str())
            .unwrap_or_default()
            .to_string(),
    })
}

/// 打开一个目录，准备枚举它的子项。
fn open_dir_handle(dir: &Path) -> Option<HANDLE> {
    let w = wide(&dir.to_string_lossy());
    // SAFETY: 同 `trace_of`；`FILE_FLAG_BACKUP_SEMANTICS` 同样是打开目录的必需品。
    let handle = unsafe {
        CreateFileW(
            w.as_ptr(),
            FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            std::ptr::null(),
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,
            std::ptr::null_mut(),
        )
    };
    if handle == INVALID_HANDLE_VALUE || handle.is_null() {
        None
    } else {
        Some(handle)
    }
}

/// 枚举一个目录下的所有子项，逐个交给 `visit`。
///
/// # 为什么要用 `GetFileInformationByHandleEx(FileIdBothDirectoryInfo)`
///
/// `std::fs::read_dir` 和 `FindFirstFileW` 都**拿不到文件索引号** ——
/// 而索引号正是这个功能要的东西。要拿它只有两条路：
///
/// 1. 对每个子项单独 `CreateFileW`（几十万次系统调用，不可接受）；
/// 2. 让内核一次把"名字 + 索引号 + 大小 + 时间 + 属性"整批填进缓冲区
///    （就是这里用的 `FileIdBothDirectoryInfo`）。
///
/// `visit` 返回 `false` 表示"够了，别再看下一个"。
///
/// # 缓冲区里那个链表为什么要手工拆
///
/// 内核返回的不是结构体数组，而是**变长记录串成的一条链**：
/// 每条记录开头有个 `NextEntryOffset`，指向下一条的偏移（最后一条是 0）。
/// 每条记录后面紧跟变长的文件名，偏移就写在 `FileName` 字段的位置上 ——
/// 所以不能按 `FILE_ID_BOTH_DIR_INFO` 的固定大小去走。
fn walk_dir(dir: &Path, visit: &mut impl FnMut(&EntryInfo) -> bool) -> bool {
    let Some(handle) = open_dir_handle(dir) else {
        return true;
    };

    // 64 KiB：一条记录最多 ~600 字节，一次能拿一百来条。
    // 用 Vec<u8> 当缓冲区，所以下面的读取必须按"未对齐"处理。
    let mut buf = vec![0u8; 64 * 1024];
    let mut keep_going = true;

    'outer: loop {
        // SAFETY: 缓冲区足够大且可写；类常量与缓冲区含义匹配。
        let ok = unsafe {
            GetFileInformationByHandleEx(
                handle,
                windows_sys::Win32::Storage::FileSystem::FileIdBothDirectoryInfo,
                buf.as_mut_ptr() as *mut _,
                buf.len() as u32,
            )
        };
        if ok == 0 {
            break;
        }

        let mut offset = 0usize;
        loop {
            if offset >= buf.len() {
                break;
            }
            // SAFETY: `offset` 由内核给出的 `NextEntryOffset` 推进，
            // 始终落在缓冲区内部（下面有边界检查）。缓冲区是 `Vec<u8>`，
            // 对齐只保证 1 字节，所以必须按未对齐方式读。
            let info: FILE_ID_BOTH_DIR_INFO =
                unsafe { std::ptr::read_unaligned(buf.as_ptr().add(offset) as *const _) };

            let name_len = info.FileNameLength as usize / 2;
            if name_len > 0 {
                // SAFETY: `FileName` 是记录里的变长尾巴，长度由
                // `FileNameLength` 给出；`offset` 之后这段内存由内核刚写满。
                let name_ptr = unsafe {
                    buf.as_ptr()
                        .add(offset)
                        .add(std::mem::offset_of!(FILE_ID_BOTH_DIR_INFO, FileName))
                        as *const u16
                };
                let name: Vec<u16> =
                    unsafe { std::slice::from_raw_parts(name_ptr, name_len) }.to_vec();
                let name = String::from_utf16_lossy(&name);

                // `.` / `..` 不是真实条目，跳过（有的文件系统实现会返回）
                if name != "." && name != ".." {
                    let is_dir = info.FileAttributes & FILE_ATTRIBUTE_DIRECTORY != 0;
                    let entry = EntryInfo {
                        name,
                        file_id: info.FileId,
                        bytes: if is_dir { 0 } else { info.EndOfFile.max(0) as u64 },
                        modified_ms: ticks_to_ms(info.LastWriteTime),
                        is_dir,
                        attributes: info.FileAttributes,
                    };
                    if !visit(&entry) {
                        keep_going = false;
                        break;
                    }
                }
            }

            if info.NextEntryOffset == 0 {
                break;
            }
            offset += info.NextEntryOffset as usize;
            // 防御：内核不可能给出越界偏移，但一旦真的越界，
            // 直接停下远好过读越界内存
            if offset >= buf.len() {
                break;
            }
        }

        if !keep_going {
            break 'outer;
        }
    }

    // SAFETY: 句柄来自 `open_dir_handle`，只关一次。
    unsafe { CloseHandle(handle) };
    keep_going
}

/// 搜索的起点，按"最可能"到"最不可能"排。
///
/// # 为什么不是直接从盘根扫
///
/// 用户挪文件有非常集中的习惯：改名（原地）、拖进同级的另一个文件夹、
/// 拖到桌面、拖到下载目录。**前两种都在原来那棵树的附近**，
/// 从最近的还在的上级往下走只需要几百毫秒；一上来就从盘根扫，
/// 一个装了几百万文件的 D 盘要扫十几秒，用户会以为卡死了。
///
/// 所以：先把近的走完，实在没有才交给盘根（仍然带预算）。
fn roots_for(hint: &str) -> Vec<PathBuf> {
    let mut roots: Vec<PathBuf> = Vec::new();
    let push = |p: PathBuf, roots: &mut Vec<PathBuf>| {
        if p.is_dir() && !roots.iter().any(|r| r == &p) {
            roots.push(p);
        }
    };

    let path = Path::new(hint.trim_matches('"'));
    let mut cur = if path.is_dir() {
        Some(path.to_path_buf())
    } else {
        path.parent().map(|p| p.to_path_buf())
    };

    // 1. 原位置所在的目录，以及它**最近还活着的**那几层祖先。
    //
    //    这一条是"文件夹被改名"那种情况的正解：
    //    `D:\Project\workbuddy\2026-09-14-20-48-18` 没了，
    //    但 `D:\Project\workbuddy` 还在 —— 从后者往下走就能看到
    //    `一键清内存工具\清内存-双击这个.bat`。
    let mut hops = 0;
    while let Some(dir) = cur {
        if dir.is_dir() {
            push(dir.clone(), &mut roots);
            hops += 1;
            // 三层足够盖住"往上退两层再换个文件夹"的常见情况
            if hops >= 3 {
                break;
            }
        }
        cur = dir.parent().map(|p| p.to_path_buf());
    }

    // 2. 用户的几个"什么都往里丢"的地方。
    if let Ok(home) = std::env::var("USERPROFILE") {
        for sub in ["Desktop", "Downloads", "Documents"] {
            push(Path::new(&home).join(sub), &mut roots);
        }
    }

    // 3. 最后才是盘根。放最后不是因为它不重要，而是因为它最贵。
    if let Some(first) = roots.first() {
        if let Some(root) = first.ancestors().last() {
            let root = root.to_path_buf();
            push(root, &mut roots);
        }
    }

    roots
}

/// 按身份找一个文件/文件夹现在在哪。
///
/// `hint` 是**原来那个路径**（哪怕它已经不存在了）—— 只用来决定从哪儿开始找。
///
/// 返回按把握程度排好序的候选：`SameFile` 一定排在 `SameContent` 前面。
pub fn find_by_trace(trace: &FileTrace, hint: &str, want_dir: bool) -> Vec<TraceHit> {
    if hint.trim().is_empty() {
        return Vec::new();
    }

    let mut exact: Vec<TraceHit> = Vec::new();
    let mut soft: Vec<TraceHit> = Vec::new();
    let started = Instant::now();
    let mut seen = 0usize;

    for root in roots_for(hint) {
        if seen >= MAX_ENTRIES || started.elapsed() > BUDGET {
            break;
        }
        // 卷不一样就不可能命中：索引号只在**本卷内**唯一，
        // 拿 A 盘的号去 B 盘比，撞上一个无关文件的风险是真实存在的。
        let Some(root_trace) = trace_of(&root.to_string_lossy()) else {
            continue;
        };
        if root_trace.volume_serial != trace.volume_serial {
            continue;
        }

        let mut queue = VecDeque::from([(root, 0usize)]);
        while let Some((dir, depth)) = queue.pop_front() {
            if seen >= MAX_ENTRIES || started.elapsed() > BUDGET {
                break;
            }

            let mut children: Vec<PathBuf> = Vec::new();
            walk_dir(&dir, &mut |entry| {
                seen += 1;
                if seen >= MAX_ENTRIES || started.elapsed() > BUDGET {
                    return false;
                }

                // 符号链接 / junction / OneDrive 占位：不进去、也不算候选。
                // 跟进去可能绕圈，也可能一步跨到别的盘甚至另一台主机上。
                if entry.attributes & 0x400 != 0 {
                    return true;
                }

                if entry.is_dir == want_dir {
                    let hit = if entry.file_id == trace.file_id {
                        Some((TraceMatch::SameFile, 100u8))
                    } else if !want_dir
                        && trace.bytes > 0
                        && entry.bytes == trace.bytes
                        && match (entry.modified_ms, trace.modified_ms) {
                            (Some(a), Some(b)) => (a - b).abs() <= 2000,
                            _ => false,
                        }
                    {
                        Some((TraceMatch::SameContent, 50u8))
                    } else {
                        None
                    };

                    if let Some((how, _)) = hit {
                        let full = dir.join(&entry.name);
                        let target = if how == TraceMatch::SameFile {
                            &mut exact
                        } else {
                            &mut soft
                        };
                        if !target.iter().any(|h| h.path == full.to_string_lossy()) {
                            target.push(TraceHit {
                                path: full.to_string_lossy().into_owned(),
                                bytes: entry.bytes,
                                modified_ms: entry.modified_ms,
                                how,
                            });
                        }
                        if exact.len() >= MAX_HITS {
                            return false;
                        }
                    }
                }

                if entry.is_dir && depth < MAX_DEPTH {
                    children.push(dir.join(&entry.name));
                }
                true
            });

            for child in children {
                queue.push_back((child, depth + 1));
            }
        }

        // 身份号命中是**确定**的，找到就不用再往下扫别的根了
        if !exact.is_empty() {
            break;
        }
    }

    for hit in &mut soft {
        hit.how = TraceMatch::SameContent;
    }
    exact.extend(soft);
    exact.truncate(MAX_HITS);
    exact
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 造一个临时目录，返回它的路径（测试结束由调用方清理）。
    fn temp_dir(tag: &str) -> PathBuf {
        let p = std::env::temp_dir().join(format!("fuguang-trace-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&p);
        std::fs::create_dir_all(&p).unwrap();
        p
    }

    #[test]
    fn 能读出一个文件的身份() {
        let dir = temp_dir("identity");
        let file = dir.join("a.txt");
        std::fs::write(&file, b"hello").unwrap();

        let t = trace_of(&file.to_string_lossy()).expect("应该能读到身份");
        assert_eq!(t.bytes, 5);
        assert_eq!(t.name, "a.txt");
        assert_ne!(t.file_id, 0, "索引号不该是 0");
        assert!(t.modified_ms.is_some());
        let _ = std::fs::remove_dir_all(&dir);
    }

    /// 这条是**整个功能的核心承诺**：改名了还能认出是同一个东西。
    #[test]
    fn 改名之后身份不变() {
        let dir = temp_dir("rename");
        let a = dir.join("旧名字.txt");
        std::fs::write(&a, b"content").unwrap();
        let before = trace_of(&a.to_string_lossy()).unwrap();

        let b = dir.join("新名字.txt");
        std::fs::rename(&a, &b).unwrap();
        let after = trace_of(&b.to_string_lossy()).unwrap();

        assert_eq!(before.file_id, after.file_id, "改名不该改变文件索引号");
        assert_eq!(before.volume_serial, after.volume_serial);
        let _ = std::fs::remove_dir_all(&dir);
    }

    /// 挪到另一个文件夹（同一个卷）身份也不变 —— 用户拖文件就是这个动作。
    #[test]
    fn 换文件夹之后身份不变() {
        let dir = temp_dir("move");
        let sub = dir.join("新家");
        std::fs::create_dir_all(&sub).unwrap();
        let a = dir.join("x.bat");
        std::fs::write(&a, b"echo hi").unwrap();
        let before = trace_of(&a.to_string_lossy()).unwrap();

        let b = sub.join("x.bat");
        std::fs::rename(&a, &b).unwrap();
        let after = trace_of(&b.to_string_lossy()).unwrap();
        assert_eq!(before.file_id, after.file_id);
        let _ = std::fs::remove_dir_all(&dir);
    }

    /// 真跑一次搜索：文件被改名 + 挪到子文件夹，只凭身份要能找回来。
    #[test]
    fn 改名又挪窝还能凭身份找回来() {
        let dir = temp_dir("find");
        let sub = dir.join("收起来了");
        std::fs::create_dir_all(&sub).unwrap();
        let a = dir.join("清内存-双击这个.bat");
        std::fs::write(&a, b"echo clear").unwrap();
        let trace = trace_of(&a.to_string_lossy()).unwrap();

        let moved = sub.join("清内存.bat");
        std::fs::rename(&a, &moved).unwrap();

        // 用**已经不存在的**原路径当提示，正是真实的调用场景
        let hits = find_by_trace(&trace, &a.to_string_lossy(), false);
        assert!(
            hits.iter().any(|h| h.how == TraceMatch::SameFile
                && h.path.ends_with("清内存.bat")),
            "应该按身份找到改名后的文件，实际：{hits:?}"
        );
        let _ = std::fs::remove_dir_all(&dir);
    }

    /// 文件夹也要能按身份找回来（它没有字节数，走的是另一条判断）。
    #[test]
    fn 文件夹改名也能找回来() {
        let dir = temp_dir("dirfind");
        let a = dir.join("2026-09-14-20-48-18");
        std::fs::create_dir_all(&a).unwrap();
        let trace = trace_of(&a.to_string_lossy()).unwrap();
        assert_eq!(trace.bytes, 0);

        let b = dir.join("一键清内存工具");
        std::fs::rename(&a, &b).unwrap();

        let hits = find_by_trace(&trace, &a.to_string_lossy(), true);
        assert!(
            hits.iter()
                .any(|h| h.how == TraceMatch::SameFile && h.path.ends_with("一键清内存工具")),
            "实际：{hits:?}"
        );
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn 不存在的路径返回空() {
        assert!(trace_of("").is_none());
        assert!(trace_of(r"C:\这个路径一定不存在\nope.exe").is_none());
    }
}
