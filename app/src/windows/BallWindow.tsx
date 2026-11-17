/**
 * 悬浮球窗口。
 *
 * 这是「测速球 + 浮光」合并后的主入口：球面上实时显示网速，
 * 点它（左键或右键）就展开浮光的完整面板。
 *
 * 交互设计：
 * - 左键单击：展开/收起主面板
 * - **右键单击：也是展开主面板** —— 这个软件只有「测速球」一个入口，
 *   右键再弹一个只有五行的原生菜单纯属多一次点击。
 *   完整菜单（新建窗口 / 数据文件夹 / 隐藏小球 / 退出）在托盘图标上，
 *   那里本来就是浮光原来的兜底入口，功能一个没丢。
 * - 按住拖动：移动小球位置
 *
 * # 为什么球变大了
 *
 * 原来是 52px，只放一个标志。现在球上要放「1.24 / MB/s」再加一行「↑1.98K ↓7.37K」，
 * 52px 连那个 1.24 都塞不下。改成 122px，与 `windows.rs` 的
 * `BALL_DIAMETER` 必须保持一致（那边要靠它算贴边位置），
 * 也与原型 `reference/netspeedball` 的 BallSize 一致 —— 球面绘制就是照它复刻的。
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { getCurrentWindow } from "@tauri-apps/api/window";

import { api, onSettingsChanged, type NetSpeedSample } from "../lib/api";
import { applyBallTheme } from "../lib/ball-theme";
import { SpeedBall } from "./SpeedBall";

/**
 * 网速轮询间隔。
 *
 * Rust 侧每 250ms 采一次样，这里 400ms 取一次 —— 刻意**不取成 250ms**：
 * 两边周期一样会撞拍（有时候一轮里取到两次同一个值、有时候跳过一个），
 * 数字看起来会一顿一顿的。取个不同的周期，每次拿到的都是新值。
 */
const POLL_INTERVAL_MS = 400;

export function BallWindow() {
  const [busy, setBusy] = useState(false);
  const [sample, setSample] = useState<NetSpeedSample | null>(null);

  /** 拖动状态。用 ref 而不是 state，避免拖动过程中触发重渲染。 */
  const drag = useRef<{ startX: number; startY: number; moved: boolean } | null>(null);

  /**
   * 订阅设置变更，实时套用悬浮球配色。
   *
   * 必须有这个订阅：小球和主面板是**两个独立窗口**，
   * 在设置页里改 CSS 变量只影响设置页自己的 DOM，
   * 小球那个窗口完全不知道 —— 实测就是这个原因导致"换了配色但球不变色"。
   *
   * 启动时的首次套用在 `main.tsx` 里做（读一次设置），
   * 这里只负责"之后被改动了"的情况。
   */
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    let disposed = false;

    void onSettingsChanged((s) => applyBallTheme(s.ballTheme)).then((fn) => {
      // 订阅是异步建立的，可能还没建立组件就卸载了
      if (disposed) fn();
      else unlisten = fn;
    });

    return () => {
      disposed = true;
      unlisten?.();
    };
  }, []);

  /**
   * 轮询网速。
   *
   * 用 `setInterval` 而不是 `requestAnimationFrame`：球上的数字每 400ms 才变一次，
   * 按帧（60fps）去问等于 95% 的请求拿回来的都是同一个值，白烧 IPC。
   *
   * 请求失败**不重试、不弹错**：采样线程没起来或者窗口正在关闭时都会失败，
   * 这两种情况都不该让用户看到一个报错框。保留上一次的数字即可。
   */
  useEffect(() => {
    let disposed = false;

    const tick = () => {
      void api
        .netspeedSample()
        .then((next) => {
          if (!disposed) setSample(next);
        })
        .catch(() => {
          /* 见上：静默保留上一次的值 */
        });
    };

    tick();
    const timer = window.setInterval(tick, POLL_INTERVAL_MS);

    return () => {
      disposed = true;
      window.clearInterval(timer);
    };
  }, []);

  /**
   * 记住小球的位置。
   *
   * 拖动是交给系统做的（`startDragging()`），我们收不到"拖完了"的回调，
   * 所以改成监听窗口移动事件 + **防抖**：拖动过程中事件会连发几十上百次，
   * 每来一次就写一遍文件太浪费。防抖窗口结束那次一定会写，
   * 所以松手之后的最终位置不会丢。
   *
   * 位置存进单独的 `window.json`（见 `api.saveBallPos` 的说明），
   * 不走设置文件——设置是整份覆盖写的，两个窗口各写一份会互相冲掉。
   */
  useEffect(() => {
    let timer: number | null = null;
    let unlisten: (() => void) | undefined;
    let disposed = false;

    void getCurrentWindow()
      .onMoved(({ payload }) => {
        if (timer !== null) window.clearTimeout(timer);
        timer = window.setTimeout(() => {
          timer = null;
          void (async () => {
            try {
              // 按**逻辑**像素存：物理像素在不同缩放比例的屏幕上不可比，
              // 换一台机器读回来就会跑到别的地方
              const scale = await getCurrentWindow().scaleFactor();
              await api.saveBallPos(payload.x / scale, payload.y / scale);
            } catch {
              /* 位置存不下来只影响"下次开机回到哪"，不该打断任何事 */
            }
          })();
        }, 400);
      })
      .then((fn) => {
        // 订阅是异步建立的，可能还没建立组件就卸载了
        if (disposed) fn();
        else unlisten = fn;
      });

    return () => {
      disposed = true;
      if (timer !== null) window.clearTimeout(timer);
      unlisten?.();
    };
  }, []);

  /**
   * 指针按下：先记下起点，但**不立即**进入拖动。
   *
   * 这里刻意不用 Tauri 的 `data-tauri-drag-region`：那个属性会吃掉 click 事件，
   * 导致左键点击无法打开面板。
   *
   * 也不能自己用 `e.screenX` 算增量去改窗口位置——那条路在缩放不为 1 的屏幕上
   * 会因逻辑/物理像素换算而累积误差，实测会把小球甩到屏幕另一头。
   * 正确做法是超过阈值后交给系统原生拖动。
   */
  const onPointerDown = useCallback((e: React.PointerEvent) => {
    if (e.button !== 0) return;
    drag.current = { startX: e.screenX, startY: e.screenY, moved: false };
  }, []);

  useEffect(() => {
    /** 超过这个像素距离才算拖动，避免手抖把点击变成移动。 */
    const DRAG_THRESHOLD = 4;

    const onMove = (e: PointerEvent) => {
      const d = drag.current;
      if (!d || d.moved) return;

      const dx = e.screenX - d.startX;
      const dy = e.screenY - d.startY;
      if (Math.abs(dx) < DRAG_THRESHOLD && Math.abs(dy) < DRAG_THRESHOLD) return;

      // 超过阈值：认定用户在拖动，交给系统原生拖动，后续移动由 Windows 接管。
      d.moved = true;
      void getCurrentWindow().startDragging();
    };

    const onUp = async () => {
      const d = drag.current;
      drag.current = null;
      if (!d) return;

      // 没超过阈值 = 单击，展开/收起面板
      if (!d.moved) {
        setBusy(true);
        try {
          await api.togglePanel();
        } finally {
          setBusy(false);
        }
      }
    };

    window.addEventListener("pointermove", onMove);
    window.addEventListener("pointerup", onUp);
    return () => {
      window.removeEventListener("pointermove", onMove);
      window.removeEventListener("pointerup", onUp);
    };
  }, []);

  /**
   * 右键：直接展开面板。
   *
   * `preventDefault()` 是必须的 —— 不拦的话 WebView2 会弹出自己的
   * 网页右键菜单（"重新加载 / 检查"那一套），在悬浮球上显得很突兀。
   */
  const onContextMenu = useCallback((e: React.MouseEvent) => {
    e.preventDefault();
    setBusy(true);
    void api
      .togglePanel()
      .catch(() => {
        /* 面板打不开时不做处理：下次点击还会再试 */
      })
      .finally(() => setBusy(false));
  }, []);

  const sample_ = sample;
  const hasAdapter = sample_?.available ?? false;

  return (
    <div className="ball-root">
      <div
        className={`ball${busy ? " ball--busy" : ""}`}
        onPointerDown={onPointerDown}
        onContextMenu={onContextMenu}
        title="左键/右键打开浮光面板 · 按住可拖动"
      >
        <SpeedBall
          down={sample_?.down ?? 0}
          up={sample_?.up ?? 0}
          available={hasAdapter}
        />
      </div>
    </div>
  );
}
