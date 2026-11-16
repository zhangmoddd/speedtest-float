/**
 * markdown 的「实时预览」——Obsidian 的 Live Preview 那一套。
 *
 * 规则只有一条：**光标停在哪一段，那一段的源码标记就露出来；光标离开，标记消失、
 * 只剩渲染后的样子。**
 *
 * 于是 `## 本章小结` 在光标不在那一行时显示成一个大标题，把光标点进去就还原成
 * `## 本章小结` 让你改。`**胶凝材料**` 同理：加粗的地方永远是粗的，只有光标
 * 落在那个词上时才看得见 `**`。
 *
 * # 为什么是「装饰」而不是「把文档换成 HTML」
 *
 * 文档本身必须**一个字都不变**。这页的正文是「点一下就粘到别的窗口」的原文，
 * 一旦为了好看把 `**` 删掉，粘贴出去的就是残缺内容。CodeMirror 的
 * `Decoration.replace` 正好满足：只在**渲染层**把标记画成零宽，文档里还在。
 *
 * # 光标判定为什么按「元素」而不是按「行」
 *
 * 按行判的话，一行里只要有五处加粗，光标落在任意位置都会把五个 `**` 全露出来，
 * 满屏都是星号。Obsidian 是按元素判的，这里跟它一致；标题是唯一的例外 ——
 * 标题的 `##` 在行首，用户的心理模型是「我在这行里」，所以按整行判。
 */
import { syntaxTree } from "@codemirror/language";
import type { EditorState, Extension, Range } from "@codemirror/state";
import {
  Decoration,
  type DecorationSet,
  EditorView,
  ViewPlugin,
  type ViewUpdate,
} from "@codemirror/view";

/**
 * 把一段源码标记整个抹掉（`##`、`**`、`` ` ``、`~~`）。
 *
 * 传空对象：不生成任何 DOM，宽度为零。这就是「看不见但还在文档里」的实现。
 */
const hidden = Decoration.replace({});

const strongMark = Decoration.mark({ class: "fg-md-strong" });
const emMark = Decoration.mark({ class: "fg-md-em" });
const codeMark = Decoration.mark({ class: "fg-md-code" });
const strikeMark = Decoration.mark({ class: "fg-md-strike" });

/**
 * 三种「成对标记」的节点名 → 各自的标记子节点名 + 该给中间那段内容套什么样式。
 *
 * 加粗、斜体、删除线的结构完全一样（一对标记夹着内容），所以用一张表统一处理，
 * 免得写三段几乎相同的代码。
 */
const PAIRED: Record<string, { markNode: string; style: Decoration }> = {
  StrongEmphasis: { markNode: "EmphasisMark", style: strongMark },
  Emphasis: { markNode: "EmphasisMark", style: emMark },
  Strikethrough: { markNode: "StrikethroughMark", style: strikeMark },
};

/** 光标或选区是否碰到了 `[from, to]` 这一段。 */
function touches(state: EditorState, from: number, to: number): boolean {
  return state.selection.ranges.some((r) => r.from <= to && r.to >= from);
}

/** 从一份文档里算出所有装饰。 */
function build(view: EditorView): DecorationSet {
  const { state } = view;
  const ranges: Range<Decoration>[] = [];
  /**
   * 已经加过行装饰的行。
   *
   * 同一个位置放两个 `Decoration.line` 是非法的（CodeMirror 会直接抛错），
   * 而 `visibleRanges` 在滚动后可能重复覆盖同一行，所以必须去重。
   * key 用 `行号:类名`：同一行可以同时是标题和引用（`> # 标题`）。
   */
  const doneLines = new Set<string>();

  const addLine = (pos: number, cls: string) => {
    const line = state.doc.lineAt(pos);
    const key = `${line.number}:${cls}`;
    if (doneLines.has(key)) return;
    doneLines.add(key);
    // 行装饰必须落在行首：从行中间开始的 line 装饰 CodeMirror 不接受
    ranges.push(Decoration.line({ class: cls }).range(line.from));
  };

  /** 给一整段（可能跨多行）的每一行加同一种样式。 */
  const addLineRange = (from: number, to: number, cls: string) => {
    const first = state.doc.lineAt(from).number;
    const last = state.doc.lineAt(Math.max(from, to - 1)).number;
    for (let n = first; n <= last; n++) addLine(state.doc.line(n).from, cls);
  };

  for (const { from, to } of view.visibleRanges) {
    syntaxTree(state).iterate({
      from,
      to,
      enter: (node) => {
        const name = node.name;

        // ---- 标题：整行换字号（`##` 的隐藏交给下面的 HeaderMark）----
        const heading = /^ATXHeading([1-6])$/.exec(name);
        if (heading) {
          addLine(node.from, `fg-md-h${heading[1]}`);
          return;
        }

        // ---- 标题的 `##` ----
        if (name === "HeaderMark") {
          const line = state.doc.lineAt(node.from);
          // 光标在这一行就露出源码：用户可能是来改标题级别的
          if (touches(state, line.from, line.to)) return;
          /**
           * `HeaderMark` 只覆盖 `##` 本身、**不含后面那个空格**。
           * 只藏 `##` 会留下一个看不见来由的空格缩进，所以把紧跟的空格一起吞掉。
           */
          let end = node.to;
          while (end < line.to && /[ \t]/.test(state.doc.sliceString(end, end + 1))) {
            end += 1;
          }
          if (end > node.from) ranges.push(hidden.range(node.from, end));
          return;
        }

        // ---- 加粗 / 斜体 / 删除线 ----
        const paired = PAIRED[name];
        if (paired) {
          const marks = node.node.getChildren(paired.markNode);
          // 只有一个标记说明语法不完整（用户正打到一半），什么都不做，
          // 让星号原样显示出来 —— 否则他会看到「刚敲的 `**` 立刻消失了」
          if (marks.length >= 2) {
            const bodyFrom = marks[0].to;
            const bodyTo = marks[marks.length - 1].from;
            if (!touches(state, node.from, node.to)) {
              for (const m of marks) {
                if (m.to > m.from) ranges.push(hidden.range(m.from, m.to));
              }
              if (bodyTo > bodyFrom) ranges.push(paired.style.range(bodyFrom, bodyTo));
            }
          }
          // 子节点已经处理完，不用再往下遍历（否则 EmphasisMark 会被重复处理）
          return false;
        }

        // ---- 行内代码 `` `x` `` ----
        if (name === "InlineCode") {
          const marks = node.node.getChildren("CodeMark");
          if (marks.length >= 2 && !touches(state, node.from, node.to)) {
            const bodyFrom = marks[0].to;
            const bodyTo = marks[marks.length - 1].from;
            for (const m of marks) {
              if (m.to > m.from) ranges.push(hidden.range(m.from, m.to));
            }
            if (bodyTo > bodyFrom) ranges.push(codeMark.range(bodyFrom, bodyTo));
          }
          return false;
        }

        // ---- 代码块：整块加底色 + 等宽字体 ----
        if (name === "FencedCode") {
          addLineRange(node.from, node.to, "fg-md-codeblock");
          // 围栏里的内容不是 markdown，别再往下解析
          return false;
        }

        // ---- 引用：整段压暗 ----
        if (name === "Blockquote") {
          addLineRange(node.from, node.to, "fg-md-quote");
          // 引用里的加粗也要生效，所以继续遍历子节点
          return;
        }
      },
    });
  }

  // `true` = 按位置排序。CodeMirror 要求 RangeSet 有序，而我们按语法树顺序
  // push 出来的结果并不严格有序（行装饰的起点在行首，可能早于当前节点）
  return Decoration.set(ranges, true);
}

/**
 * 把上面那套装饰挂进编辑器的插件。
 *
 * 重建时机只有四种：文档变了、光标动了、滚出新的可见区、焦点进出。
 * 别的一律不重建 —— 装饰重建会动 DOM，代价不小。
 */
export const livePreview: Extension = ViewPlugin.fromClass(
  class {
    decorations: DecorationSet;

    constructor(view: EditorView) {
      this.decorations = build(view);
    }

    update(update: ViewUpdate) {
      // 文档变了但还**没**重建装饰时（比如输入法正在组合），先把旧装饰按
      // 变更映射过去：不映射的话位置会整体错位，样式闪到别的字上
      if (update.docChanged) {
        this.decorations = this.decorations.map(update.changes);
      }

      /**
       * 输入法（中文拼音）正在组合时**不重建**。
       *
       * 重建会把组合中的那段文字对应的 DOM 换掉，WebView2 里的组合态随之断开，
       * 表现就是「打拼音打到一半，候选词没了、字直接落屏」。
       * 组合结束时会再来一次 update，那时自然恢复。
       */
      if (update.view.composing) return;

      if (
        update.docChanged ||
        update.selectionSet ||
        update.viewportChanged ||
        update.focusChanged ||
        /**
         * 最后这条是给**语法解析**留的。
         *
         * markdown 的语法树不是同步解析的：敲完 `## ` 的那一瞬间树还是旧的，
         * 解析器在空闲时补完，然后发一个**空 transaction**（`view.dispatch({})`）
         * 通知一声。只认 docChanged 的话，标题要等下一次击键才会变大。
         */
        update.transactions.length > 0
      ) {
        this.decorations = build(update.view);
      }
    }
  },
  {
    decorations: (plugin) => plugin.decorations,
  },
);
