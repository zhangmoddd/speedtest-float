/**
 * 悬浮球本体：照 `reference/netspeedball/NetSpeedBall.cs` 的 GDI+ 绘制
 * 逐条搬过来的 SVG 版本。
 *
 * # 为什么不能直接"复制"原版代码
 *
 * 原版是 C# 的 GDI+ 手绘，跑在 WinForms 里；这个外壳是 Tauri（WebView2），
 * 界面是网页。GDI+ 的 `Graphics` / `PathGradientBrush` / `DrawArc` 在网页里
 * 没有对应物，代码搬不过来。所以只能按它的**参数**在 SVG 里重建 ——
 * 下面每个数字都能在 C# 源码里找到出处，注释里标了函数名。
 *
 * # 尺寸约定
 *
 * viewBox 用原版 GDI+ 的**像素坐标**（122×122，球体矩形 (5,5,111,111)），
 * 这样每个数字都能和 C# 源码逐行对上，改的时候不用换算。
 *
 * # 动态部分
 *
 * 进度环弧长、火箭位置/角度、尾焰长度走动画循环直接改 DOM 属性，
 * 不走 React 状态 —— 60fps 的重渲染会把整个 SVG diff 一遍，
 * 而实际变的只有三个属性。
 */

import { useEffect, useRef } from "react";

/** 球体矩形，对应 C# 的 `new Rectangle(5, 5, Width - 11, Height - 11)`。 */
const BOX = 5;
const BOX_SIZE = 111;
const CX = BOX + BOX_SIZE / 2;
const CY = BOX + BOX_SIZE / 2;
const R = BOX_SIZE / 2;

/** 进度环半径：C# 的 `Rectangle.Inflate(rect, -6, -6)`。 */
const PROGRESS_R = R - 6;

/** 火箭轨道半径：C# 的 `rect.Width * 0.37f`。 */
const ROCKET_RADIUS = BOX_SIZE * 0.37;

/** 低于这个下载速率按空闲处理（8 KB/s），C# 的 `RocketIdleThresholdBytesPerSecond`。 */
const IDLE_THRESHOLD = 8 * 1024;

/** 火箭满速参考值：8 MB/s。 */
const REFERENCE_SPEED = 8 * 1024 * 1024;

/** 满速角速度（度/秒），C# 的 `strength * 156.0f`。 */
const MAX_ORBIT_SPEED = 156;

/** 火箭初始角度，C# 的 `rocketAngle = -58.0f`。 */
const START_ANGLE = -58;

// ---------------------------------------------------------------
// 字体：全部照 C# 的 Font 定义换算（磅 × 96/72 = 像素）
// ---------------------------------------------------------------

/** C# `speedFont`：Segoe UI 25pt Bold → 33.3px。 */
const VALUE_SIZE = 33.3;
/** C# `unitFont`：Segoe UI 8.8pt → 11.7px。 */
const UNIT_SIZE = 11.7;
/** C# `statFont`：Segoe UI 10.5pt Bold → 14px。 */
const STAT_VALUE_SIZE = 14;
/** C# `statArrowFont`：Segoe UI 12.5pt Bold → 16.7px。 */
const STAT_ARROW_SIZE = 16.7;
/** C# 缩字号的硬下限：`Math.Max(8.0f, ...)` 与 `Math.Max(9.5f, ...)`。 */
const STAT_VALUE_MIN = 8;
const STAT_ARROW_MIN = 9.5;

/** 数字行允许的最大宽度：C# 的 `Width - 34`。 */
const STAT_MAX_WIDTH = 122 - 34;
/** 箭头与数字之间、两组之间的间距：C# 的 `arrowGap = 1.5f` / `pairGap = 7.0f`。 */
const ARROW_GAP = 1.5;
const PAIR_GAP = 7;

const FONT_STACK = '"Segoe UI", "Microsoft YaHei UI", system-ui, sans-serif';

/** 文字基线。对应 C# 里 `DrawString(..., y)` 的 top-left 语义换算出来的位置。 */
const BASELINE_VALUE = 59;
const BASELINE_UNIT = 76;
const BASELINE_STAT = 95;

/**
 * 全局复用一个 canvas 2D 上下文，专门用来量文字宽度。
 *
 * 为什么必须实测而不是估算：C# 用 `Graphics.MeasureString` 量宽度决定要不要缩字号。
 * 我先用「字符宽度表」估算过一版，估大了 20% 左右，导致字号缩得过头、
 * 数字明显比原版小一圈。`measureText` 走同一个字体引擎，量出来才准。
 */
let measureCtx: CanvasRenderingContext2D | null = null;
function getMeasureCtx(): CanvasRenderingContext2D | null {
  if (measureCtx) return measureCtx;
  if (typeof document === "undefined") return null;
  const canvas = document.createElement("canvas");
  measureCtx = canvas.getContext("2d");
  return measureCtx;
}

/** 量一段文字在指定字号下的宽度。量不到时退回一个保守估算。 */
function measureText(text: string, size: number): number {
  const ctx = getMeasureCtx();
  if (!ctx) return text.length * size * 0.6;
  ctx.font = `700 ${size}px ${FONT_STACK}`;
  return ctx.measureText(text).width;
}

// ---------------------------------------------------------------
// 数字格式化：逐条照抄 C# 的 SpeedFormatter
// ---------------------------------------------------------------

/**
 * 去掉小数末尾多余的 0（以及只剩小数点的情况）。
 *
 * 对应 C# 的 `ToString("0.##")` —— 它把 1.20 输出成 "1.2"、把 10.0 输出成 "10"。
 * JS 的 `toFixed` 不做这件事，所以得手动剥。
 */
function trimZeros(s: string): string {
  return s.includes(".") ? s.replace(/0+$/, "").replace(/\.$/, "") : s;
}

/**
 * C# 的 `FormatNumber`：
 * ≥100 取整、≥10 一位小数、其余两位小数，末尾 0 一律去掉。
 */
function formatNumber(value: number): string {
  if (!Number.isFinite(value) || value < 0) return "0";
  if (value >= 100) return value.toFixed(0);
  if (value >= 10) return trimZeros(value.toFixed(1));
  return trimZeros(value.toFixed(2));
}

/**
 * 球面正中的大数字 + 单位。
 *
 * 对应 C# 的 `FormatMegabytesRate`：**永远按 MB/s 显示**，
 * 即使只有 0.04 MB/s 也不换成 KB/s。这是原版刻意的选择 ——
 * 单位固定，数字才能纵向对齐比较，不会一会儿 K 一会儿 M 地跳。
 */
function formatMain(bytesPerSecond: number): { value: string; unit: string } {
  const clean = Number.isFinite(bytesPerSecond) && bytesPerSecond > 0 ? bytesPerSecond : 0;
  return { value: formatNumber(clean / (1024 * 1024)), unit: "MB/s" };
}

/**
 * 数字行用的紧凑格式。
 *
 * 对应 C# 的 `FormatCompact`：先除以 1024 变成 K，超过 1024 才进位成 M。
 * 注意它**不会**退到 B —— 0 也显示成 "0K"。
 */
function formatCompact(bytesPerSecond: number): string {
  const clean = Number.isFinite(bytesPerSecond) && bytesPerSecond > 0 ? bytesPerSecond : 0;
  const kb = clean / 1024;
  if (kb >= 1024) return `${formatNumber(kb / 1024)}M`;
  return `${formatNumber(kb)}K`;
}

/**
 * 由下载速率算「火箭推力」，0~1。对应 C# 的 `GetRocketFlameStrength()`。
 *
 * 对数刻度：8 KB/s 以下为 0，8 MB/s 及以上为 1。
 * 用对数是因为用户对「24K 和 1M 的差别」的感受远大于「7M 和 8M」。
 */
export function rocketStrength(down: number): number {
  if (!Number.isFinite(down) || down <= 0) return 0;
  const raw = Math.log(1 + down / 8192) / Math.log(1 + REFERENCE_SPEED / 8192);
  const threshold = Math.log(1 + IDLE_THRESHOLD / 8192) / Math.log(1 + REFERENCE_SPEED / 8192);
  return Math.max(0, Math.min(1, (raw - threshold) / (1 - threshold)));
}

/**
 * 活跃度 0~1，决定进度环弧长。对应 C# 的 `GetActivityRatio()`。
 * 对数刻度，参考值 2 MB/s；有流量时下限 0.08，上限 1。
 */
export function activityRatio(down: number, up: number): number {
  const bytes = Math.max(down, up);
  if (!Number.isFinite(bytes) || bytes <= 0) return 0;
  const ratio = Math.log(bytes + 1) / Math.log(2 * 1024 * 1024 + 1);
  return Math.min(1, Math.max(0.08, ratio));
}

/** 尾焰路径，与 C# `DrawRocket` 里两段 AddBezier 一一对应。 */
function flamePath(length: number, halfWidth: number): string {
  const tip = -10 - length;
  return [
    `M -9.5 ${-halfWidth}`,
    `C -12 ${-halfWidth * 0.8} ${tip} -1 ${tip} 0`,
    `C ${tip} 1 -12 ${halfWidth * 0.8} -9.5 ${halfWidth}`,
    "Z",
  ].join(" ");
}

/**
 * 椭圆弧 path，对应 GDI+ 的 `DrawArc(pen, x, y, w, h, startAngle, sweepAngle)`。
 *
 * GDI+ 角度从 3 点钟方向顺时针算、y 轴向下，与 SVG 一致，
 * 所以起点/终点直接用三角函数算，弧的 sweep 标志恒为 1。
 */
function ellipseArc(
  cx: number,
  cy: number,
  rx: number,
  ry: number,
  startDeg: number,
  sweepDeg: number,
): string {
  const rad = (d: number) => (d * Math.PI) / 180;
  const sx = cx + rx * Math.cos(rad(startDeg));
  const sy = cy + ry * Math.sin(rad(startDeg));
  const ex = cx + rx * Math.cos(rad(startDeg + sweepDeg));
  const ey = cy + ry * Math.sin(rad(startDeg + sweepDeg));
  const largeArc = sweepDeg > 180 ? 1 : 0;
  return `M ${sx.toFixed(2)} ${sy.toFixed(2)} A ${rx} ${ry} 0 ${largeArc} 1 ${ex.toFixed(2)} ${ey.toFixed(2)}`;
}

/** 云朵轮廓三段弧，坐标与 C# 的三次 DrawArc 调用逐条对应。 */
const CLOUD_EDGES = [
  ellipseArc(29, 108, 17, 14, 194, 126),
  ellipseArc(46.5, 104, 16.5, 17, 190, 128),
  ellipseArc(95, 106, 18, 16, 210, 112),
];

interface Props {
  /** 下行速率，字节/秒 */
  down: number;
  /** 上行速率，字节/秒 */
  up: number;
  /** 是否有可用网卡。false 时显示「离线 / WAITING」，与 C# 一致 */
  available: boolean;
}

/** 数字行里的一段文字（一个箭头或一个数字），带自己的字号与横坐标。 */
interface Segment {
  text: string;
  x: number;
  size: number;
  color: string;
}

/**
 * 排版数字行：量宽 → 必要时缩字号 → 从左到右累加横坐标。
 *
 * 这是 C# `DrawStatPair` 里 `MeasureStatRow` + `DrawStatSegment` 两步的直译。
 * 顺序是「↑ 上行 ↓ 下行」，上行绿、下行蓝。
 */
function layoutStatRow(upText: string, downText: string): Segment[] {
  let arrowSize = STAT_ARROW_SIZE;
  let valueSize = STAT_VALUE_SIZE;

  const totalWidth = () =>
    measureText("↑", arrowSize) +
    ARROW_GAP +
    measureText(upText, valueSize) +
    PAIR_GAP +
    measureText("↓", arrowSize) +
    ARROW_GAP +
    measureText(downText, valueSize);

  let width = totalWidth();
  if (width > STAT_MAX_WIDTH) {
    const scale = STAT_MAX_WIDTH / width;
    arrowSize = Math.max(STAT_ARROW_MIN, STAT_ARROW_SIZE * scale);
    valueSize = Math.max(STAT_VALUE_MIN, STAT_VALUE_SIZE * scale);
    width = totalWidth();
  }

  const UP_COLOR = "rgb(16,198,94)";
  const DOWN_COLOR = "rgb(35,119,238)";
  const segments: Segment[] = [];
  let x = (122 - width) / 2;

  const push = (text: string, size: number, color: string) => {
    segments.push({ text, x, size, color });
    x += measureText(text, size) + ARROW_GAP;
  };

  push("↑", arrowSize, UP_COLOR);
  push(upText, valueSize, UP_COLOR);
  x += PAIR_GAP - ARROW_GAP;
  push("↓", arrowSize, DOWN_COLOR);
  push(downText, valueSize, DOWN_COLOR);

  return segments;
}

export function SpeedBall({ down, up, available }: Props) {
  const rocketRef = useRef<SVGGElement | null>(null);
  const flameRef = useRef<SVGPathElement | null>(null);
  const flameCoreRef = useRef<SVGEllipseElement | null>(null);

  /** 当前火箭角度。用 ref 存，避免每帧 setState 触发重渲染。 */
  const angleRef = useRef(START_ANGLE);
  const strengthRef = useRef(0);
  /** 上一次推进角度的时刻（毫秒），rAF 与兜底定时器共用。 */
  const lastTickRef = useRef(0);

  // 把最新网速交给动画循环读（不重启循环）
  strengthRef.current = available ? rocketStrength(down) : 0;

  useEffect(() => {
    /**
     * 推进一帧。
     *
     * # 为什么角度按「时间差 × 角速度」而不是「每帧加固定值」
     *
     * 每帧加固定值的话，一旦掉帧（窗口被遮挡、系统繁忙）转速就变慢，
     * 用户看到的是"火箭走着走着就慢了/不动了"。按真实时间差算，
     * 掉多少帧都不影响转速。
     */
    const step = (now: number) => {
      if (lastTickRef.current === 0) lastTickRef.current = now;
      // 单次最大步长 50ms：窗口被遮挡后恢复时时间差可能是几秒，
      // 不夹住的话火箭会瞬间甩出去一大截
      const dt = Math.min(0.05, (now - lastTickRef.current) / 1000);
      lastTickRef.current = now;

      const strength = strengthRef.current;
      if (strength > 0 && dt > 0) {
        angleRef.current += strength * MAX_ORBIT_SPEED * dt;
        if (angleRef.current > 360) angleRef.current -= 360;
      }

      const rad = (angleRef.current * Math.PI) / 180;
      const x = CX + Math.cos(rad) * ROCKET_RADIUS;
      const y = CY + Math.sin(rad) * ROCKET_RADIUS;

      rocketRef.current?.setAttribute(
        "transform",
        `translate(${x.toFixed(2)} ${y.toFixed(2)}) rotate(${(angleRef.current + 90).toFixed(2)}) scale(0.86)`,
      );

      // 尾焰：C# 的 visibleFlame —— 有推力才出现，最小 0.36
      const visible = strength > 0 ? Math.max(0.36, strength) : 0;
      const flame = flameRef.current;
      const core = flameCoreRef.current;
      if (flame && core) {
        if (visible > 0) {
          const pulse = 0.88 + 0.12 * Math.sin(angleRef.current * 0.24);
          const length = (8 + 16 * visible) * pulse;
          const halfWidth = 2 + 2.8 * visible;
          flame.setAttribute("d", flamePath(length, halfWidth));
          flame.setAttribute("opacity", String(0.69 + 0.31 * visible));
          core.setAttribute("opacity", String(0.59 + 0.41 * visible));
          const coreRx = (3 + length * 0.35) / 2;
          core.setAttribute("rx", String(coreRx));
          core.setAttribute("cx", String(-12 - length * 0.42 + coreRx));
        } else {
          flame.setAttribute("opacity", "0");
          core.setAttribute("opacity", "0");
        }
      }
    };

    let raf = 0;
    const loop = (now: number) => {
      step(now);
      raf = requestAnimationFrame(loop);
    };
    raf = requestAnimationFrame(loop);

    /**
     * 兜底定时器。
     *
     * # 为什么光有 rAF 不够
     *
     * 悬浮球窗口永远拿不到焦点（它只是个球），而 Chromium 在窗口被遮挡、
     * 最小化或系统进入省电模式时会把 `requestAnimationFrame` 降频甚至暂停。
     * 表现就是「火箭走着走着就不动了」，而网速明明还在跑 —— 这正是实测遇到的 bug。
     *
     * 用 400ms 定时器兜底：即使 rAF 被暂停，角度也按真实时间继续推进
     * （看起来一顿一顿的），rAF 恢复后立刻回到 60fps 的顺滑。
     * 两处共用 `lastTickRef`，不会重复累加。
     */
    const fallback = window.setInterval(() => step(performance.now()), 400);

    return () => {
      cancelAnimationFrame(raf);
      window.clearInterval(fallback);
    };
  }, []);

  const main = formatMain(down);
  const upText = formatCompact(up);
  const downText = formatCompact(down);
  const segments = layoutStatRow(upText, downText);

  // 进度环：起始 126°，扫过 max(32, 74 + activity*190) 度（C# 的 DrawBall）
  const activity = available ? activityRatio(down, up) : 0;
  const sweep = Math.max(32, 74 + activity * 190);
  const circumference = 2 * Math.PI * PROGRESS_R;
  const dash = (circumference * sweep) / 360;

  return (
    <svg
      className="speedball"
      width="122"
      height="122"
      viewBox="0 0 122 122"
      role="img"
      aria-label={available ? `下载 ${downText}，上传 ${upText}` : "网络未连接"}
    >
      <defs>
        {/* 球面：中心点偏到 (48%, 38%)，这是原版 PathGradientBrush 的参数。
            放正中间会变成平面圆盘，没有"球"的感觉 */}
        <radialGradient id="sb-face" cx="48%" cy="38%" r="72%">
          <stop offset="0%" stopColor="#ffffff" />
          <stop offset="100%" stopColor="rgb(236,243,250)" />
        </radialGradient>

        {/* 上半部分的高光，叠在球面上做"上亮下暗" */}
        <linearGradient id="sb-sheen" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor="#ffffff" stopOpacity="0.47" />
          <stop offset="100%" stopColor="#ffffff" stopOpacity="0" />
        </linearGradient>

        {/* 顶部集中的反光团 */}
        <radialGradient id="sb-gloss" cx="50%" cy="50%" r="50%">
          <stop offset="0%" stopColor="#ffffff" stopOpacity="0.6" />
          <stop offset="100%" stopColor="#ffffff" stopOpacity="0" />
        </radialGradient>

        {/* 云朵只画在球体内部 */}
        <clipPath id="sb-clip">
          <circle cx={CX} cy={CY} r={R} />
        </clipPath>
      </defs>

      {/* 1. 球面本体 */}
      <circle cx={CX} cy={CY} r={R} fill="url(#sb-face)" />
      {/* 2. 上半高光 */}
      <circle cx={CX} cy={CY} r={R} fill="url(#sb-sheen)" />
      {/* 3. 顶部反光团：C# 的 (rect.X+15, rect.Y+8, Width-30, 34) */}
      <ellipse cx={CX} cy={BOX + 8 + 17} rx={(BOX_SIZE - 30) / 2} ry={17} fill="url(#sb-gloss)" />

      {/* 4. 底部云朵。坐标逐条对应 C# 的 DrawClouds */}
      <g clipPath="url(#sb-clip)">
        <g fill="rgb(246,251,255)">
          <ellipse cx={29} cy={108} rx={17} ry={14} />
          <ellipse cx={46.5} cy={104} rx={16.5} ry={17} />
          <ellipse cx={70} cy={108} rx={17} ry={14} />
          <ellipse cx={95} cy={106} rx={18} ry={16} />
          <rect x={16} y={106} width={89} height={24} />
        </g>
        {/* 右侧云朵的阴影，让它有体积 */}
        <ellipse cx={92} cy={114.5} rx={18} ry={9.5} fill="rgba(221,235,244,0.86)" />
        {/* 云朵轮廓。没有这三段弧，云和球面同色，等于看不见 */}
        {CLOUD_EDGES.map((d, i) => (
          <path key={i} d={d} fill="none" stroke="rgba(198,217,232,0.56)" strokeWidth={1} />
        ))}
      </g>

      {/* 5. 三圈描边：外发光 / 外圈 / 内圈 */}
      <circle cx={CX} cy={CY} r={R - 4} fill="none" stroke="rgba(70,174,232,0.46)" strokeWidth={5} />
      <circle cx={CX} cy={CY} r={R} fill="none" stroke="rgba(250,253,255,0.96)" strokeWidth={2.4} />
      <circle cx={CX} cy={CY} r={R - 7} fill="none" stroke="rgba(132,196,232,0.8)" strokeWidth={1.1} />

      {/* 6. 进度环：从 126° 开始顺时针扫 sweep 度。
          用 stroke-dasharray 而不是 path 弧线，弧长变化时不用重算路径 */}
      <circle
        cx={CX}
        cy={CY}
        r={PROGRESS_R}
        fill="none"
        stroke={available ? "rgba(38,143,233,0.83)" : "rgba(164,183,205,0.35)"}
        strokeWidth={2.3}
        strokeLinecap="round"
        strokeDasharray={`${dash} ${circumference - dash}`}
        transform={`rotate(126 ${CX} ${CY})`}
      />

      {/* 7. 火箭。transform 由动画循环直接改，React 不接管这个属性 */}
      <g ref={rocketRef} transform={`translate(${CX} ${CY}) rotate(${START_ANGLE}) scale(0.86)`}>
        <path ref={flameRef} d="" fill="rgba(255,137,42,0.75)" opacity="0" />
        <ellipse
          ref={flameCoreRef}
          cx={-14}
          cy={0}
          rx={2.5}
          ry={1.35}
          fill="rgba(255,231,116,0.7)"
          opacity="0"
        />
        {/* 尾翼 */}
        <path d="M -5.4 -3.4 L -9.8 -8 L -2 -5.1 Z" fill="rgb(45,132,224)" stroke="rgba(22,91,176,0.69)" strokeWidth={0.8} />
        <path d="M -5.4 3.4 L -9.8 8 L -2 5.1 Z" fill="rgb(45,132,224)" stroke="rgba(22,91,176,0.69)" strokeWidth={0.8} />
        {/* 机身 */}
        <path
          d="M -9 -5.2 C -3.8 -8 5.2 -5 9.2 0 C 5.2 5 -3.8 8 -9 5.2 Z"
          fill="#ffffff"
          stroke="rgba(82,144,215,0.75)"
          strokeWidth={1}
        />
        {/* 舷窗 */}
        <circle cx={3.2} cy={0} r={2.6} fill="rgb(39,132,227)" stroke="rgba(255,255,255,0.82)" strokeWidth={0.8} />
      </g>

      {/* 8. 文字。位置对应 C# 的 DrawSpeedText / DrawStatPair */}
      {available ? (
        <>
          <text
            x={CX}
            y={BASELINE_VALUE}
            textAnchor="middle"
            fontSize={VALUE_SIZE}
            fontWeight={700}
            fill="rgb(20,27,42)"
            className="speedball__num"
          >
            {main.value}
          </text>
          <text
            x={CX}
            y={BASELINE_UNIT}
            textAnchor="middle"
            fontSize={UNIT_SIZE}
            fill="rgb(70,84,102)"
          >
            {main.unit}
          </text>
          {segments.map((seg, i) => (
            <text
              key={i}
              x={seg.x}
              y={BASELINE_STAT}
              fontSize={seg.size}
              fontWeight={700}
              fill={seg.color}
              className="speedball__num"
            >
              {seg.text}
            </text>
          ))}
        </>
      ) : (
        <>
          <text
            x={CX}
            y={55}
            textAnchor="middle"
            fontSize={VALUE_SIZE}
            fontWeight={700}
            fill="rgb(28,34,48)"
            className="speedball__num"
          >
            离线
          </text>
          <text x={CX} y={72} textAnchor="middle" fontSize={UNIT_SIZE} fill="rgb(104,118,136)">
            WAITING
          </text>
        </>
      )}
    </svg>
  );
}
