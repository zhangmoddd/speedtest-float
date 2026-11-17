//! 单实例 + 「显示面板」信号。
//!
//! # 为什么需要它
//!
//! 合并之后，球由 C# 的「网络测速球」提供，浮光只负责面板。
//! 用户在球上右键点「打开浮光」时，如果浮光已经在后台（面板被 ✕ 收起来了），
//! 不应该再起一个进程 —— 那会变成两个面板、两份内存、两套托盘。
//!
//! 做法是**命名事件**（named event）：
//! - 第一个实例启动时创建 `Local\FuguangShowPanel`，并起一个线程等它被 Set
//! - 之后的实例发现事件已存在，就 Set 一下（等于"喂，把面板显示出来"），自己退出
//!
//! 用 `Local\` 前缀而不是 `Global\`：跨登录会话没有必要，而且 `Global\`
//! 需要额外权限，普通用户可能创建失败。
//!
//! C# 那边对应的是 `EventWaitHandle.OpenExisting(@"Local\FuguangShowPanel")`，
//! 两边的名字必须一字不差。

#[cfg(windows)]
pub use imp::{signal_existing, spawn_listener};

#[cfg(not(windows))]
pub fn signal_existing() -> bool {
    false
}

#[cfg(not(windows))]
pub fn spawn_listener(_app: tauri::AppHandle) {}

#[cfg(windows)]
mod imp {
    use std::ptr;
    use std::thread;

    use windows_sys::Win32::Foundation::{CloseHandle, HANDLE};
    use windows_sys::Win32::System::Threading::{
        CreateEventW, OpenEventW, SetEvent, WaitForSingleObject, EVENT_MODIFY_STATE,
    };

    /// 事件名。**必须与 C# 侧 `EventWaitHandle.OpenExisting` 的参数一致。**
    const EVENT_NAME: &str = "Local\\FuguangShowPanel";

    /// 等待时用的大超时（约 49 天），等价于 INFINITE 但不依赖那个常量的导入路径。
    const WAIT_FOREVER: u32 = 0xFFFF_FFFF;

    fn wide(s: &str) -> Vec<u16> {
        s.encode_utf16().chain(std::iter::once(0)).collect()
    }

    /// 把裸句柄包一层，好让它能跨线程移动。
    ///
    /// Win32 的事件句柄本来就是进程级的、可以在任意线程使用
    /// （`WaitForSingleObject` 就是设计成在别的线程上等的），
    /// 只是 `*mut c_void` 这个类型本身没有实现 `Send`。
    /// 这里手写 `unsafe impl Send` 就是在告诉编译器「我知道它在跨线程时是安全的」。
    struct SendHandle(HANDLE);

    // SAFETY: 见上面的说明 —— Win32 句柄不绑定线程。
    unsafe impl Send for SendHandle {}

    impl SendHandle {
        /// 取出裸句柄。
        ///
        /// ⚠️ 必须走方法而不是直接写 `handle.0`：Rust 2021 的闭包捕获是**按字段**的，
        /// 写 `handle.0` 会让闭包直接捕获那个 `*mut c_void` 字段本身
        /// （它不是 Send，编译报 E0277），`unsafe impl Send` 就白写了。
        /// 通过方法访问，闭包才会整体捕获 `SendHandle`。
        fn raw(&self) -> HANDLE {
            self.0
        }
    }

    /// 已经有实例在跑就通知它显示面板并返回 `true`（调用方应当直接退出）。
    ///
    /// 打不开事件说明没有实例在跑（或者上一次的实例刚退出、事件还没释放），
    /// 这时返回 `false`，走正常启动流程。
    pub fn signal_existing() -> bool {
        let name = wide(EVENT_NAME);
        // SAFETY: name 是合法的 NUL 结尾宽字符串；句柄用完立刻关闭。
        unsafe {
            let handle = OpenEventW(EVENT_MODIFY_STATE, 0, name.as_ptr());
            if handle.is_null() {
                return false;
            }
            SetEvent(handle);
            CloseHandle(handle);
        }
        true
    }

    /// 创建事件并起一个后台线程等信号，收到就把面板显示出来。
    pub fn spawn_listener(app: tauri::AppHandle) {
        let name = wide(EVENT_NAME);
        // SAFETY: name 合法；事件句柄交给后台线程持有，进程退出时由系统回收。
        let handle: HANDLE = unsafe { CreateEventW(ptr::null(), 0, 0, name.as_ptr()) };
        if handle.is_null() {
            return;
        }
        let handle = SendHandle(handle);

        thread::spawn(move || loop {
            // SAFETY: handle 由上面的 CreateEventW 创建，线程内独占使用。
            let rc = unsafe { WaitForSingleObject(handle.raw(), WAIT_FOREVER) };
            // WAIT_OBJECT_0 == 0；其它返回值（失败/超时）都当作"再等一轮"
            if rc != 0 {
                continue;
            }
            // 窗口操作必须回到主线程做，否则在非主线程建窗会出问题。
            // 这里要 clone 两次：一次给 run_on_main_thread 的调用方，
            // 一次给闭包内部 —— 只 clone 一次会同时被"借用"和"移动"（E0505）。
            let for_call = app.clone();
            let for_closure = app.clone();
            let _ = for_call.run_on_main_thread(move || {
                if let Err(err) = crate::windows::show_panel(&for_closure, None) {
                    crate::diag!("[浮光] 显示面板失败：{err}");
                }
            });
        });
    }
}
