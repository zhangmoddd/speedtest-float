import { describe, expect, it } from "vitest";

import { markSegments } from "./search-highlight";

/** 把切段结果压成好读的字符串，方便看：命中用 [ ] 包起来。 */
function render(text: string, query: string): string {
  return markSegments(text, query)
    .map((s) => (s.hit ? `[${s.text}]` : s.text))
    .join("");
}

describe("markSegments", () => {
  it("没有关键词时整段原样返回", () => {
    expect(markSegments("云服务器C2A密钥", "")).toEqual([
      { text: "云服务器C2A密钥", hit: false },
    ]);
    expect(markSegments("云服务器C2A密钥", "   ")).toEqual([
      { text: "云服务器C2A密钥", hit: false },
    ]);
  });

  it("空文字返回空数组", () => {
    expect(markSegments("", "ef")).toEqual([]);
  });

  it("命中一次", () => {
    expect(render("ef31c9", "ef")).toBe("[ef]31c9");
  });

  it("同一个词出现多次，每处都标", () => {
    expect(render("ef-ef31", "ef")).toBe("[ef]-[ef]31");
  });

  it("大小写不敏感，但保留原文的大小写", () => {
    expect(render("EF31c0", "ef")).toBe("[EF]31c0");
    expect(render("ef31C0", "EF")).toBe("[ef]31C0");
  });

  it("多个词各自标，顺序无关", () => {
    expect(render("github 账号 ef", "ef github")).toBe("[github] 账号 [ef]");
  });

  it("重叠的命中合并成一段，不出现相邻的两个 mark", () => {
    expect(render("abc", "ab bc")).toBe("[abc]");
  });

  it("相邻但不重叠的命中保持两段", () => {
    // `ab` 结束在 2，`cd` 从 2 开始：合并成 [abcd]，接缝处不会有细缝
    expect(render("abcd", "ab cd")).toBe("[abcd]");
  });

  it("词里的正则特殊字符按字面处理", () => {
    expect(render("a.*b 正常", ".*")).toBe("a[.*]b 正常");
    expect(render("C:\\Users\\x", "\\u")).toBe("C:[\\U]sers\\x");
    expect(render("价格(元)", "(")).toBe("价格[(]元)");
  });

  it("没命中就一段都不标", () => {
    expect(render("云服务器C2A密钥", "zzz")).toBe("云服务器C2A密钥");
  });

  it("命中在开头和结尾都正确切分", () => {
    expect(render("ef中间ef", "ef")).toBe("[ef]中间[ef]");
  });

  it("拼接回来的文字与原文完全一致（不丢字、不错位）", () => {
    const text = "2026-10-06 · kerchief-yoga-path@duck.com";
    const segments = markSegments(text, "yoga duck");
    expect(segments.map((s) => s.text).join("")).toBe(text);
  });
});
