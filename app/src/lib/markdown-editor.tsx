/**
 * markdown 编辑器：一个长得像输入框、但会把 markdown 渲染出来的东西。
 *
 * 为什么不用 `<textarea>`：textarea 只能显示纯文本，`**加粗**` 永远是四个星号。
 * 要做到「光标移开就渲染、移上去就露源码」，必须换成真正的编辑器内核
 * （CodeMirror 6）——它知道每个字符属于 markdown 的哪一段语法，
 * 才能把 `##`、`**` 这些标记单独藏起来。
 *
 * # 一个硬约束：文档内容必须是原文
 *
 * 这页的正文是「点一下就粘贴到别的窗口」的东西。所以渲染只发生在**显示层**
 * （见 `markdown-live-preview.ts`），`value` / `onChange` 里流传的永远是
 * 用户敲的 markdown 原文。复制、粘贴、搜索走的都是原文。
 */
import { useEffect, useRef } from "react";
import { Annotation, EditorState } from "@codemirror/state";
import {
  EditorView,
  keymap,
  placeholder as placeholderExt,
} from "@codemirror/view";
import { defaultKeymap, history, historyKeymap, indentWithTab } from "@codemirror/commands";
import { markdown, markdownLanguage } from "@codemirror/lang-markdown";

import { api } from "./api";
import type { ContextMenuItem, UseContextMenuResult } from "./context-menu";
import { livePreview } from "./markdown-live-preview";

import "./markdown-editor.css";

/**
 * 给「这次内容是外面塞进来的」打个记号。
 *
 * 必须有：外面的 `value` 变了（比如点了「撤销改动」）要把新内容写进编辑器，
 * 写入会触发 `updateListener`，而 `onChange` 又会把内容推回外面 ——
 * 一圈下来光标会被重置、还可能把外面的状态覆盖成中间态。
 * 打了记号就能在监听器里认出「这次是我自己写的，别再往外报」。
 */
const ExternalSync = Annotation.define<boolean>();

/** 读剪贴板里的文字。读不到返回 `null`（和 `lib/context-menu.tsx` 同一条策略）。 */
async function readClipboardText(): Promise<string | null> {
  try {
    const text = await api.readClipboardText();
    // 命令成功但剪贴板里没有文字：不再试浏览器那条路（它只会再失败一次）
    if (text !== null) return text;
  } catch {
    // 命令不可用（旧后端）→ 落到浏览器兜底
  }
  try {
    return await navigator.clipboard.readText();
  } catch {
    return null;
  }
}

export interface MarkdownEditorProps {
  value: string;
  /** 内容变了。收到的是 markdown **原文**。 */
  onChange: (text: string) => void;
  placeholder?: string;
  /** 挂载后自动聚焦（新建笔记时用）。 */
  autoFocus?: boolean;
  /** 「键入到当前光标」：把选中的那段文字打到外部窗口。 */
  onTypeInto: (text: string) => void;
  /** 右键菜单里那些"顺手做一下"的结果提示。 */
  onNotice: (text: string, kind: "ok" | "warn") => void;
  /** 上层已经建好的右键菜单实例（整个页面一份）。 */
  contextMenu: UseContextMenuResult;
}

export function MarkdownEditor({
  value,
  onChange,
  placeholder,
  autoFocus,
  onTypeInto,
  onNotice,
  contextMenu,
}: MarkdownEditorProps) {
  const hostRef = useRef<HTMLDivElement | null>(null);
  const viewRef = useRef<EditorView | null>(null);

  /**
   * 回调放 ref 里：编辑器只在挂载时创建一次，如果直接把 `onChange` 闭包进去，
   * 它永远是第一帧那个函数，读到的 `form` 也是第一帧的。
   */
  const onChangeRef = useRef(onChange);
  onChangeRef.current = onChange;
  const valueRef = useRef(value);
  valueRef.current = value;

  useEffect(() => {
    const parent = hostRef.current;
    if (!parent) return;

    const view = new EditorView({
      parent,
      state: EditorState.create({
        doc: valueRef.current,
        extensions: [
          // Ctrl+Z / Ctrl+Y 的撤销栈。textarea 自带这个，换成编辑器就得自己装
          history(),
          keymap.of([...defaultKeymap, ...historyKeymap, indentWithTab]),
          markdown({ base: markdownLanguage }),
          // 面板只有 420px 宽，不自动换行的话长句子会横向滚成一条线
          EditorView.lineWrapping,
          EditorState.tabSize.of(2),
          placeholderExt(placeholder ?? ""),
          livePreview,
          EditorView.updateListener.of((update) => {
            if (!update.docChanged) return;
            if (update.transactions.some((t) => t.annotation(ExternalSync))) return;
            onChangeRef.current(update.state.doc.toString());
          }),
        ],
      }),
    });

    viewRef.current = view;
    if (autoFocus) view.focus();

    return () => {
      view.destroy();
      viewRef.current = null;
    };
    // 只建一次：`placeholder` / `autoFocus` 变了也不重建编辑器（重建会丢光标）
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  /**
   * 外部内容变了 → 写进编辑器。
   *
   * 只有「撤销改动」这类动作会走到这里（打字时两边本来就一致）。
   * 先比一次再写：内容相同还 dispatch，会白白重置一次光标和撤销栈。
   */
  useEffect(() => {
    const view = viewRef.current;
    if (!view) return;
    const current = view.state.doc.toString();
    if (current === value) return;
    view.dispatch({
      changes: { from: 0, to: current.length, insert: value },
      annotations: ExternalSync.of(true),
    });
  }, [value]);

  /**
   * 右键菜单：和 textarea 那套功能一致（键入到光标 / 复制 / 剪切 / 粘贴 / 全选），
   * 只是取选区、改内容都要走 CodeMirror 的 API。
   */
  const onContextMenu = (e: React.MouseEvent) => {
    const view = viewRef.current;
    if (!view) return;
    e.preventDefault();

    // 右键落在选区外面时先把光标挪过去：不挪的话「复制」复制的是上一次选中的东西，
    // 而用户以为复制的是他刚点的那个词
    const pos = view.posAtCoords({ x: e.clientX, y: e.clientY });
    if (pos !== null) {
      const sel = view.state.selection.main;
      if (pos < sel.from || pos > sel.to) {
        view.dispatch({ selection: { anchor: pos } });
      }
    }

    const sel = view.state.selection.main;
    const selected = view.state.sliceDoc(sel.from, sel.to);
    const hasSelection = selected.length > 0;

    /**
     * 改选区内容。
     *
     * 走 `dispatch` 而不是像 textarea 那样硬改 DOM：这样改动会进撤销栈，
     * 用户按 Ctrl+Z 能撤回这次粘贴 / 剪切（textarea 版做不到，那是它的已知取舍）。
     */
    const replaceSelection = (text: string) => {
      const range = view.state.selection.main;
      view.dispatch({
        changes: { from: range.from, to: range.to, insert: text },
        selection: { anchor: range.from + text.length },
      });
      view.focus();
    };

    const items: ContextMenuItem[] = [
      {
        id: "type",
        label: "键入到当前光标",
        disabled: !hasSelection,
        hint: hasSelection ? undefined : "先选中文字",
        onSelect: () => onTypeInto(selected),
      },
      {
        id: "copy",
        label: "复制",
        hint: "Ctrl+C",
        disabled: !hasSelection,
        dividerBefore: true,
        onSelect: () => {
          void api.copyText(selected).then((ok) => {
            onNotice(
              ok ? "已复制到剪贴板" : "复制失败，剪贴板可能被占用",
              ok ? "ok" : "warn",
            );
          });
        },
      },
      {
        id: "cut",
        label: "剪切",
        hint: "Ctrl+X",
        disabled: !hasSelection,
        onSelect: () => {
          void api.copyText(selected).then((ok) => {
            if (!ok) {
              // 写剪贴板失败时**不删正文**：那等于把用户的字弄丢了
              onNotice("剪切失败：剪贴板被占用，正文没有改动", "warn");
              return;
            }
            replaceSelection("");
            onNotice("已剪切到剪贴板", "ok");
          });
        },
      },
      {
        id: "paste",
        label: "粘贴",
        hint: "Ctrl+V",
        onSelect: () => {
          void readClipboardText().then((text) => {
            if (text === null) {
              onNotice("读不到剪贴板，请用 Ctrl+V 粘贴", "warn");
              return;
            }
            if (text === "") {
              onNotice("剪贴板里没有文字", "warn");
              return;
            }
            replaceSelection(text);
          });
        },
      },
      {
        id: "select-all",
        label: "全选",
        hint: "Ctrl+A",
        dividerBefore: true,
        onSelect: () => {
          view.dispatch({
            selection: { anchor: 0, head: view.state.doc.length },
          });
          view.focus();
        },
      },
    ];

    contextMenu.open(e, () => items);
  };

  return <div className="fg-md" ref={hostRef} onContextMenu={onContextMenu} />;
}
