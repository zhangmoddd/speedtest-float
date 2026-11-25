//! 「足迹」：记住一个文件/文件夹的**身份**，它改了名、搬了家之后还能被找回来。
//!
//! # 为什么路径靠不住
//!
//! 链接现在把"目标在哪"存成一个字符串。而路径只是**当前位置的地址**，
//! 不是**这个东西本身**。文件夹一改名，整条路径就作废：
//!
//! ```text
//! 原来：D:\工作\2026-09-14-20-48-18\启动-双击这个.bat
//! 后来：D:\工作\清理小工具\启动-双击这个.bat
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
    CreateFileW, GetDriveTypeW, GetFileInformationByHandle, GetFileInformationByHandleEx,
    GetLogicalDrives, BY_HANDLE_FILE_INFORMATION, FILE_ATTRIBUTE_DIRECTORY,
    FILE_FLAG_BACKUP_SEMANTICS, FILE_ID_BOTH_DIR_INFO, FILE_LIST_DIRECTORY, FILE_READ_ATTRIBUTES,
    FILE_SHARE_DELETE, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING,
};

/// `GetDriveTypeW` 的返回值。常量在 windows-sys 里门控在别的模块下，
/// 为两个数字引一整个绑定模块不划算。
const DRIVE_REMOVABLE: u32 = 2;
const DRIVE_FIXED: u32 = 3;

/// 所有**本地**盘的根（固定盘 + 可移动盘）。
///
/// # 为什么跨盘也要扫
///
/// 文件被拖到另一个盘时，它的身份号在那边是个**全新的号**（跨盘移动
/// 在文件系统层面就是"复制 + 删除"），按身份对不上。但**大小和修改时间
/// 会被复制过程保留下来** —— 那是唯一还能跨盘工作的线索。
///
/// 不扫别的盘，就等于"文件一挪到 C 盘，这个功能就废了"，
/// 而用户完全不知道自己触发了这个边界。
///
/// # 为什么**明确排除**网络盘和光驱
///
/// - **网络盘**（`DRIVE_REMOTE`）：对一个陌生 UNC / 映射盘做目录枚举
///   就是把当前用户的 NetNTLM 响应发出去，可以离线破解。
///   这条路径是"用户点一下修复"触发的，不该有任何网络动作 ——
///   和 `launcher::probe` 里拦的是同一件事。
/// - **光驱**：没放盘时 `is_dir()` / 枚举会卡到超时，几秒钟白等。
fn local_drive_roots() -> Vec<PathBuf> {
    // SAFETY: `GetLogicalDrives` 只读一个位图，没有参数也没有副作用。
    let mask = unsafe { GetLogicalDrives() };
    let mut out = Vec::new();
    for i in 0..26u32 {
        if mask & (1 << i) == 0 {
            continue;
        }
        let letter = (b'A' + i as u8) as char;
        let root = format!("{letter}:\\");
        let w = wide(&root);
        // SAFETY: `w` 以 NUL 结尾且在本轮内有效。
        let kind = unsafe { GetDriveTypeW(w.as_ptr()) };
        if kind == DRIVE_FIXED || kind == DRIVE_REMOVABLE {
            out.push(PathBuf::from(root));
        }
    }
    out
}

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
            let name_start = offset + std::mem::offset_of!(FILE_ID_BOTH_DIR_INFO, FileName);
            // 内核不可能给出越界的 FileNameLength；万一真的越界，就当这条没有名字
            // （和 name_len == 0 走同一个跳过分支），而不是去碰缓冲区外面的内存。
            if name_len > 0 && name_start + name_len * 2 <= buf.len() {
                // `FileName` 是记录里的变长尾巴，这段内存由内核刚写满。
                //
                // 这里**不**把指针转成 `*const u16` 再切片：缓冲区是 `Vec<u8>`，
                // 只保证 1 字节对齐，而 `&[u16]` 要求 2 字节对齐 —— 上面那条
                // `read_unaligned` 正是为了这个，不能再从旁边绕过去。
                // 按字节对读回来（Windows 是小端），结果完全一样，且没有 unsafe。
                let name: Vec<u16> = buf[name_start..name_start + name_len * 2]
                    .chunks_exact(2)
                    .map(|pair| u16::from_le_bytes([pair[0], pair[1]]))
                    .collect();
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
    //    `D:\工作\2026-09-14-20-48-18` 没了，
    //    但 `D:\工作` 还在 —— 从后者往下走就能看到
    //    `清理小工具\启动-双击这个.bat`。
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

    // 3. 盘根，**无条件**加，而且摆到**最前面**。
    //
    //    ⚠️ 这里原来是 `if let Some(first) = roots.first()` —— 也就是
    //    "连一层祖上都找不到就放弃"。那正好漏掉唯一还能找的地方：
    //    原路径整个被删/改名时 roots 可能是空的，而用户把文件拖到盘根
    //    （`D:\启动-双击这个.bat`）是非常常见的动作，实测就撞上了。
    //
    //    ⚠️ 为什么还要挪到最前面（实测数据）：盘根只有几十个目录项，
    //    扫它几乎免费；而它后面的祖先可能是几万项的巨型目录。
    //    排在后面时，用户点一下「找找看」要等 **1.5 秒**才出结果，
    //    排到前面就是几十毫秒 —— 屏幕上是"转一下就好了"和
    //    "卡了一下，是不是死了"的区别。
    let drive_root = Path::new(hint.trim_matches('"'))
        .ancestors()
        .last()
        .map(|p| p.to_path_buf());
    if let Some(root) = drive_root.filter(|r| r.is_dir()) {
        roots.retain(|r| r != &root);
        roots.insert(0, root);
    }

    // 4. 其他本地盘的根，排在最后。
    //
    //    这一档只服务"跨盘移动"（身份号会变，只剩大小 + 时间那条线索），
    //    命中率比上面几档低，所以放最后 —— 上面的根扫完没结果才轮到它。
    //    成本很低：每个盘的**第一层**只有几十个目录项。
    for root in local_drive_roots() {
        push(root, &mut roots);
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

    // ---- 所有起点放进**同一个**队列，按深度一层层铺开 ----
    //
    // ⚠️ 这里原来是"逐个根挖到底，挖完一个再挖下一个"，实测撞了个正着：
    //
    //   用户先把文件挪进 `D:\工作\清理小工具\`，链接跟着改好；
    //   后来又把文件拖到了 `D:\` 的根目录。这时往上退出来的根依次是
    //   `D:\工作`（两万多个条目）、`D:\Project`（更多）、`D:\`（几十个）。
    //   串行扫的话，前两个根就把条目预算吃光了，`D:\` **一次都没轮到** ——
    //   界面上就是"找不到"，而文件就在最浅的那一层，一眼就能看见。
    //
    // 合并成一个队列之后，`D:\` 的第一层会和 `D:\工作` 的第一层
    // 一起被扫。**便宜的根先出结果，贵的根不会把它挡在后面。**
    // 队列第三项：这个根和目标**是不是同一个卷**。
    //
    // - 同卷 → 可以按身份号判（`SameFile`，确定）
    // - 跨卷 → 身份号没有可比性（同一对数字在另一个卷上可能指向
    //   一个完全无关的文件），只允许"大小 + 修改时间"这条弱线索，
    //   而且**不进子目录**：跨盘只扫每个盘根的第一层，
    //   否则就是拿 C 盘做全盘搜索了。
    let mut queue: VecDeque<(PathBuf, usize, bool)> = VecDeque::new();
    for root in roots_for(hint) {
        let Some(root_trace) = trace_of(&root.to_string_lossy()) else {
            continue;
        };
        let same_volume = root_trace.volume_serial == trace.volume_serial;
        queue.push_back((root, 0usize, same_volume));
    }

    'scan: while let Some((dir, depth, same_volume)) = queue.pop_front() {
        if seen >= MAX_ENTRIES || started.elapsed() > BUDGET || !exact.is_empty() {
            break;
        }

        let mut children: Vec<PathBuf> = Vec::new();
        let keep_going = walk_dir(&dir, &mut |entry| {
            seen += 1;
            if seen >= MAX_ENTRIES || started.elapsed() > BUDGET {
                return false;
            }
            // 已经按身份号锁定了，**立刻停**。
            //
            // 身份号命中是确定的：就是它，不会再有"另一个同一个人"。
            // 继续扫只会让用户多等 —— 实测这条真实用例里，命中发生在
            // 盘根的第一层（几十个目录项），但没有这一句的话，
            // 搜索还会把后面那几个几万项的祖先目录全部扫完，
            // 结果是「明明几十毫秒就该出结果，用户盯着转圈看了一秒多」。
            if !exact.is_empty() {
                return false;
            }

            // 符号链接 / junction / OneDrive 占位：不进去、也不算候选。
            // 跟进去可能绕圈，也可能一步跨到别的盘甚至另一台主机上。
            if entry.attributes & 0x400 != 0 {
                return true;
            }
            // 隐藏 / 系统项：回收站（`$RECYCLE.BIN`）、`System Volume Information`
            // 里也会有一堆同大小同时间的文件。扫它们既慢又只会造出误判候选。
            if entry.attributes & (0x2 | 0x4) != 0 {
                return true;
            }

            if entry.is_dir == want_dir {
                let how = if same_volume && entry.file_id == trace.file_id {
                    Some(TraceMatch::SameFile)
                } else if !want_dir
                    && trace.bytes > 0
                    && entry.bytes == trace.bytes
                    && match (entry.modified_ms, trace.modified_ms) {
                        (Some(a), Some(b)) => (a - b).abs() <= 2000,
                        _ => false,
                    }
                {
                    Some(TraceMatch::SameContent)
                } else {
                    None
                };

                if let Some(how) = how {
                    let text = dir.join(&entry.name).to_string_lossy().into_owned();
                    let bucket = if how == TraceMatch::SameFile {
                        &mut exact
                    } else {
                        &mut soft
                    };
                    if !bucket.iter().any(|h| h.path == text) {
                        bucket.push(TraceHit {
                            path: text,
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

            // 跨卷的根（别的盘）**不进子目录**：进去了就是全盘搜索，
            // 而这条线索（大小 + 时间）本来就不够硬，不值得为它付那个代价。
            if entry.is_dir && depth < MAX_DEPTH && same_volume {
                children.push(dir.join(&entry.name));
            }
            true
        });

        for child in children {
            queue.push_back((child, depth + 1, same_volume));
        }
        if !keep_going {
            break 'scan;
        }
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
        let a = dir.join("启动-双击这个.bat");
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

        let b = dir.join("清理小工具");
        std::fs::rename(&a, &b).unwrap();

        let hits = find_by_trace(&trace, &a.to_string_lossy(), true);
        assert!(
            hits.iter()
                .any(|h| h.how == TraceMatch::SameFile && h.path.ends_with("清理小工具")),
            "实际：{hits:?}"
        );
        let _ = std::fs::remove_dir_all(&dir);
    }

    /// ⚠️ 这条钉的是一个**用户实测撞上的失败**。
    ///
    /// 真实过程：文件先被挪进 `D:\工作\清理小工具\`（链接跟着改好），
    /// 后来又被人拖到 `D:\` 的根目录。这时往上退出来的起点是
    /// `…\workbuddy`（两万多个条目）、`…\Project`（更多）、`D:\`（几十个）。
    ///
    /// 改动之前是"逐个根挖到底"：前两个巨型的根把条目预算吃光，
    /// `D:\` 一次都没轮到 —— 界面上写"找不到"，而文件就在最浅的那一层。
    ///
    /// 这里刻意把**又深又杂的目录**和**浅层的目标**摆在一起，
    /// 逼着搜索必须先把便宜的那一层走完。
    #[test]
    fn 深目录挡路时浅层的目标也要能找到() {
        let dir = temp_dir("shallow-wins");
        let deep = dir.join("很深的目录");
        std::fs::create_dir_all(deep.join("里")).unwrap();
        // 造一堆同级杂物，让这个分支"很贵"
        for i in 0..400 {
            let p = deep.join(format!("杂物{i}"));
            std::fs::create_dir_all(&p).unwrap();
            std::fs::write(p.join("填充.txt"), b"x").unwrap();
        }

        // 目标在浅层（相当于用户把它拖到了盘根那一层）
        let target = dir.join("启动-双击这个.bat");
        std::fs::write(&target, b"echo clear").unwrap();
        let trace = trace_of(&target.to_string_lossy()).unwrap();

        let moved = dir.join("搬走后改的名字.bat");
        std::fs::rename(&target, &moved).unwrap();

        // 链接里存的还是那个深层的、已经不存在的路径
        let stale = deep.join("不存在").join("启动-双击这个.bat");
        let hits = find_by_trace(&trace, &stale.to_string_lossy(), false);

        assert!(
            hits.iter()
                .any(|h| h.how == TraceMatch::SameFile && h.path.ends_with("搬走后改的名字.bat")),
            "应该按身份找到移到浅层的文件，实际：{hits:?}"
        );
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn 不存在的路径返回空() {
        assert!(trace_of("").is_none());
        assert!(trace_of(r"C:\这个路径一定不存在\nope.exe").is_none());
    }
}
