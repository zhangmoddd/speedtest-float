/**
 * 生成 `dist/mock.html` —— 一个能在普通浏览器里渲染出悬浮球的校验页。
 *
 * # 为什么需要它
 *
 * 悬浮球的外观是 SVG 画的，跟 Tauri / WebView2 没关系。所以在浏览器里
 * 就能验证「画得像不像原版」，不必每次都编译 Rust、启动整个桌面程序。
 * 排查界面问题时这是最快的回路。
 *
 * # 为什么不在 public/ 里放一个静态文件
 *
 * Vite 每次 build 都会清空 dist，而 index.html 引用的 JS/CSS 文件名带内容哈希
 * （`index-DZTsUsn1.js`），静态文件里写不出这个哈希。所以改成构建后现生成：
 * 从 dist/index.html 里读出真实文件名再填进去。
 *
 * 用法：`npm run build && node scripts/make-mock.mjs`
 */
import { readFileSync, writeFileSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const dist = resolve(root, "dist");

const index = readFileSync(resolve(dist, "index.html"), "utf8");
const js = index.match(/src="([^"]+\.js)"/)?.[1];
const css = index.match(/href="([^"]+\.css)"/)?.[1];
if (!js || !css) {
  console.error("读不出 dist/index.html 里的资源路径，构建可能失败了");
  process.exit(1);
}

/**
 * 假的 Tauri 后端。
 *
 * 只实现前端真正会调的两个命令：读设置、取网速。其它一律返回 null ——
 * 这个页面不测功能，只测「球长什么样」，所以不需要完整的后端。
 */
const html = `<!doctype html>
<html lang="zh-CN">
  <head>
    <meta charset="UTF-8" />
    <title>悬浮球渲染校验</title>
    <script>
      const FAKE = {
        settings_get: { fontSizePx: 14, ballTheme: "white" },
        netspeed_sample: { down: 1258291, up: 202752, downTotal: 0, upTotal: 0, available: true },
      };
      window.__TAURI_INTERNALS__ = {
        invoke: (cmd) => Promise.resolve(Object.prototype.hasOwnProperty.call(FAKE, cmd) ? FAKE[cmd] : null),
        transformCallback: (cb) => {
          window.__cb = window.__cb || {};
          const id = Math.floor(Math.random() * 1e9);
          window.__cb[id] = cb;
          return id;
        },
        unregisterCallback: () => {},
        convertFileSrc: (p) => p,
        metadata: { currentWindow: { label: "ball" }, currentWebview: { label: "ball" } },
      };
      location.hash = "#/ball";
    </script>
    <script type="module" crossorigin src="${js}"></script>
    <link rel="stylesheet" crossorigin href="${css}" />
    <style>
      html, body, #root { margin: 0; height: 100%; background: #dfe6ee; }
    </style>
  </head>
  <body>
    <div id="root"></div>
  </body>
</html>
`;

writeFileSync(resolve(dist, "mock.html"), html, "utf8");
console.log("已生成 dist/mock.html ->", js);
