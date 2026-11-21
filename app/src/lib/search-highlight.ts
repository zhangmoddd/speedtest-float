/**
 * 搜索结果里的关键词高亮：把一段文字切成「命中 / 没命中」的片段。
 *
 * # 为什么单独一个文件
 *
 * 这段逻辑全是纯字符串判断，而且在组件里写不出来就测不了（项目的 vitest 跑在
 * `node` 环境，没有 jsdom）。而它出错的方式又特别隐蔽：高亮错位、段落丢字、
 * 关键词被切开——界面上只会表现成"看着有点怪"，不会报错。
 *
 * # 规则
 *
 * 1. **大小写不敏感**：搜 `ef` 要能标出 `EF31C`。
 * 2. **多个词各自标**：命令面板支持空格分词（`github 账号`），
 *    两个词在标题和副标题里都该亮起来。
 * 3. **重叠 / 相邻的命中合并成一段**：搜 `ab bc` 时 `abc` 只该是一个高亮块，
 *    拆成两个相邻的 `<mark>` 会在接缝处出现一条细缝。
 * 4. **不用正则**：用户会往搜索框里打 `(`、`*`、`\` 这些字符，
 *    用 `new RegExp` 拼关键词迟早炸在特殊字符上（或者更糟：悄悄匹配错位置）。
 *    这里全程 `indexOf`。
 */

/** 一段文字：`hit` 为真表示这一段命中了关键词。 */
export interface TextSegment {
  text: string;
  /** 这一段是否该高亮。 */
  hit: boolean;
}

/**
 * 把 `text` 按 `query` 切段。
 *
 * 命中区间是按**原文下标**算的，所以 `text.slice()` 切出来的永远是原字符，
 * 不会因为大小写转换丢字。
 *
 * `query` 为空（或全是空白）时整段返回一个 `hit: false` —— 调用方可以无脑渲染。
 */
export function markSegments(text: string, query: string): TextSegment[] {
  if (!text) return [];

  const tokens = query.trim().toLowerCase().split(/\s+/).filter(Boolean);
  if (tokens.length === 0) return [{ text, hit: false }];

  const lower = text.toLowerCase();
  /**
   * 大小写转换会改变长度的极少数字符（`İ` → `i̇`）会让下标错位。
   * 碰到了就放弃高亮而不是切错字 —— 文字正确比高亮重要。
   */
  if (lower.length !== text.length) return [{ text, hit: false }];

  const ranges: Array<[number, number]> = [];
  for (const token of tokens) {
    let from = 0;
    for (;;) {
      const at = lower.indexOf(token, from);
      if (at < 0) break;
      ranges.push([at, at + token.length]);
      // 从命中结束处继续找：同一个词在 "aaa" 里搜 "aa" 只标一次，
      // 而不是标出两个重叠的块
      from = at + token.length;
    }
  }
  if (ranges.length === 0) return [{ text, hit: false }];

  ranges.sort((a, b) => a[0] - b[0]);
  const merged: Array<[number, number]> = [];
  for (const range of ranges) {
    const last = merged[merged.length - 1];
    if (last && range[0] <= last[1]) {
      if (range[1] > last[1]) last[1] = range[1];
    } else {
      merged.push([range[0], range[1]]);
    }
  }

  const out: TextSegment[] = [];
  let cursor = 0;
  for (const [start, end] of merged) {
    if (start > cursor) out.push({ text: text.slice(cursor, start), hit: false });
    out.push({ text: text.slice(start, end), hit: true });
    cursor = end;
  }
  if (cursor < text.length) out.push({ text: text.slice(cursor), hit: false });
  return out;
}
