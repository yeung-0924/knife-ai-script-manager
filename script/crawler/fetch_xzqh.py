#!/usr/bin/env python3
# 更新时间: 2026-09-14 18:09:00
# 全国行政区划导出（省 / 地 / 县 / 乡）→ CSV / TXT / JSON / SQL（MySQL 兼容）
#
# 数据源：民政部·国家地名信息库  https://dmfw.mca.gov.cn/9095/xzqh/getList
# 仅依赖 Python 标准库（3.7+），无需 pip 安装任何东西。
#
# 参数（由 index.json 的 params 声明，界面填写，程序按占位符代入）：
#   -ExportDir  导出目录（必填）      结果文件写入该目录
#   -Code       省市县区代码（可选）  留空 = 导出全部；填写 = 只导出该区划及其下级
#   -Level      导出级别（可选）      省市县区 / 乡镇街道（后者 = 省市县区 + 乡镇街道）
#   -Type       导出类型（必填）      csv / txt / json / sql
#
# 产物（一律按编码正序排序；仅「省市县区」时文件名不带 _l4 后缀）：
#   china_xzqh[_l4].csv            16 列，UTF-8 带 BOM，Excel 双击可开
#   china_xzqh[_l4].txt            制表符分隔（同 16 列），UTF-8 带 BOM
#   china_xzqh[_l4].json           JSON 数组（字段名同 CSV 表头），UTF-8 无 BOM
#   china_xzqh[_l4].sql            建表 + 列注释 + 索引 + 分批 INSERT（MySQL 5.7+/8.0 可直接执行）
#
# ★ 接口四条铁律（沿用已验证的抓取逻辑，勿改）
#   1) 一次请求最多返回「从 code 起点向下 3 层」的子树，maxLevel 上限为 3：
#        起点 = 省级 → 省 / 地 / 县（够不到乡级）
#        起点 = 地级 → 市 / 县 / 乡      ★ 这是拿乡级的关键
#      故抓乡级只需 333 个地级 + 118 个「父级为省级」的县级 = 451 次请求（约 15 秒），
#      绝不逐县下钻（那是 2847 次）。
#   2) 顶层请求必须完全不带 code 参数（?maxLevel=3）；写成 ?code=&maxLevel=3 会静默漏掉台湾省。
#   3) 接口有频率限制：密集调用会返回 HTTP 200 但内容为 status=2「接口调用过于频繁」，
#      必须校验 status == 200 且 data 非空并退避重试。默认并发 2 + 每请求间隔 0.18s。
#   4) 台湾省不返回数字编码（原为中文「资料暂缺」），按其 GB/T 2260 标准码 710000 补齐；
#      否则中文混进编号列，入库直接失败（港澳为 810000 / 820000，与接口一致）。
#
# 编码口径：接口代码为 12 位（有效码 + 补零）；落盘按层级截断——
#           区县及以上 6 位（GB/T 2260，如 110101），乡级 9 位（GB/T 10114，如 110101001）。
#           请求接口时仍传 12 位原码（传 6 位会静默返回顶层全量）。
#
# 缓存：默认 %LOCALAPPDATA%\knife-script-manager\cache\xzqh（放在导出目录之外，避免污染产物），
#       7 天过期。接口数据每年才更新一次，缓存若永不过期会静默拿到旧数据；删掉缓存目录即强制重抓。
import argparse
import csv
import datetime
import json
import os
import random
import ssl
import sys
import tempfile
import time
import urllib.parse
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed

SRC_URL = "https://dmfw.mca.gov.cn/9095/xzqh/getList"

# 占位符未被替换时的特征（用户留空该参数）
PLACEHOLDER_MARK = "_p{"

# ---- 日志：ANSI 前景色 + 标识前缀（遵循项目 script-output-style 规范）----
ESC = "\033"
GREEN, YELLOW, RED, RESET = f"{ESC}[92m", f"{ESC}[93m", f"{ESC}[91m", f"{ESC}[0m"


def say(color, tag, msg=""):
    print(f"{color}[{tag}]{RESET} {msg}".rstrip() if msg else f"{color}[{tag}]{RESET}", flush=True)


def param(msg):
    say(GREEN, "入参", msg)


def result(msg):
    say(GREEN, "结果", msg)


def info(msg):
    say(YELLOW, "信息", msg)


def err(msg):
    say(RED, "异常", msg)


def step(i, total, text):
    print(f"===== [{i}/{total}] {text} =====", flush=True)


def is_unset(value):
    """参数是否仍为未替换的占位符（或为空）。"""
    return not value or PLACEHOLDER_MARK in value


def update_time():
    """从脚本自身头部的「更新时间」注释取时间戳，随日志打印，便于判断脚本版本新旧。"""
    try:
        with open(__file__, encoding="utf-8") as f:
            for line in f:
                if "更新时间:" in line:
                    return line.split("更新时间:")[1].strip()
    except Exception:                                                # noqa: BLE001
        pass
    return ""


def _reconfig_stdout():
    """避免个别字符无法编码导致整个脚本崩掉。"""
    try:
        sys.stdout.reconfigure(errors="replace")
    except Exception:                                                # noqa: BLE001
        pass


def fmt(n):
    return "{:,}".format(n)


def bar(done, total, width=24):
    frac = (float(done) / total) if total else 0.0
    n = int(frac * width + 0.5)
    return "[%s%s] %3d%%" % ("#" * n, "-" * (width - n), int(frac * 100 + 0.5))


def default_cache_dir():
    """默认缓存目录：放系统本地应用数据下，不写进用户的导出目录。"""
    base = os.environ.get("LOCALAPPDATA") or os.environ.get("TEMP") or tempfile.gettempdir()
    return os.path.join(base, "knife-script-manager", "cache", "xzqh")


# --------------------------------------------------------------------------
# 请求与缓存
# --------------------------------------------------------------------------
ctx = ssl.create_default_context()
ctx.check_hostname = False
ctx.verify_mode = ssl.CERT_NONE
HDR = {
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                  "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
    "Referer": "https://dmfw.mca.gov.cn/",
    "Accept": "application/json, text/plain, */*",
}

LEVEL_NAME = {1: "省级", 2: "地级", 3: "县级", 4: "乡级"}
COLS = ["code", "name", "admin_level", "level_name", "division_type",
        "parent_code", "parent_name", "province_code", "province_name",
        "city_code", "city_name", "county_code", "county_name",
        "full_name", "is_leaf", "fetch_date"]
FETCH_DATE = datetime.date.today().isoformat()

# 接口未提供数字编码的行政区 → GB/T 2260 标准码（不臆造：与港澳 810000 / 820000 同体系）
CODE_FALLBACK = {(1, "台湾省"): "710000"}
UNMAPPED = []       # 非数字编码且未配置补齐规则的节点（应恒为空）


def fetch_json(url, tries=8, base_delay=1.0, timeout=120):
    """请求并校验；遇限流（status=2）或异常时退避重试。"""
    delay = base_delay
    last = None
    for _ in range(tries):
        try:
            req = urllib.request.Request(url, headers=HDR)
            with urllib.request.urlopen(req, timeout=timeout, context=ctx) as r:
                d = json.loads(r.read().decode("utf-8"))
            if d.get("status") == 200 and (d.get("data") or {}).get("level") is not None:
                return d
            last = "status=%s message=%s" % (d.get("status"), d.get("message"))
        except Exception as e:                                       # noqa: BLE001
            last = "%s: %s" % (type(e).__name__, e)
        time.sleep(delay + random.uniform(0, 0.4))
        delay = min(delay * 1.8, 8.0)
    raise RuntimeError("请求失败（已重试 %d 次）：%s\n  url=%s" % (tries, last, url))


class Cache(object):
    """只有校验通过的响应才落缓存；坏缓存自动删除重抓；超期缓存强制重抓。

    ★ 该接口的数据每年只更新一次，若缓存永不过期，明年重跑会静默拿到旧数据。
      故默认 7 天过期（--cache-ttl 可调，0 = 永不过期）。
    """

    def __init__(self, cache_dir, ttl_days=7):
        self.dir = cache_dir
        self.ttl = int(ttl_days) * 86400 if ttl_days and ttl_days > 0 else 0
        self.hits = 0
        self.misses = 0
        self.expired = 0
        if cache_dir:
            os.makedirs(cache_dir, exist_ok=True)

    @staticmethod
    def _load(path):
        try:
            with open(path, "r", encoding="utf-8") as f:
                d = json.load(f)
            if d.get("status") == 200 and (d.get("data") or {}).get("level") is not None:
                return d
        except Exception:                                            # noqa: BLE001
            pass
        return None

    def get(self, key, url, sleep_s=0.0):
        p = os.path.join(self.dir, key + ".json") if self.dir else None
        if p and os.path.exists(p) and os.path.getsize(p) > 0:
            if self.ttl and (time.time() - os.path.getmtime(p)) > self.ttl:
                self.expired += 1                                    # 超期：忽略缓存，重新抓
            else:
                d = self._load(p)
                if d is not None:
                    self.hits += 1
                    return d
                os.remove(p)                                         # 坏缓存：删掉重抓
        self.misses += 1
        d = fetch_json(url)
        if p:
            with open(p, "w", encoding="utf-8") as f:
                json.dump(d, f, ensure_ascii=False)
        if sleep_s:
            time.sleep(sleep_s)
        return d


# --------------------------------------------------------------------------
# 解析
# --------------------------------------------------------------------------
def norm_code(level, code12, name=None):
    """12 位接口码 → 标准码：乡级 9 位，其余 6 位。

    非数字编码（接口对台湾省返回「资料暂缺」）按 CODE_FALLBACK 补标准码；
    补不上的记入 UNMAPPED 并返回空串（宁可少一行，也不让中文进编号列）。
    """
    c = (code12 or "").strip()
    if c.isdigit():
        if len(c) == 12 and c.endswith("000"):
            return c[:9] if level == 4 else c[:6]
        return c
    hit = CODE_FALLBACK.get((level, name))
    if hit:
        return hit
    if c:
        UNMAPPED.append((level, name, c))
    return ""


def walk_tree(node, chain, sink):
    """深度优先遍历，chain 为祖先链（含当前节点），元素同时带 code12 与标准码 code。"""
    lv = node.get("level")
    if lv and lv >= 1:
        chain = chain + [{"level": lv, "code12": node.get("code"),
                          "code": norm_code(lv, node.get("code"), node.get("name")),
                          "name": node.get("name"), "type": node.get("type") or ""}]
        sink(node, chain)
    for ch in (node.get("children") or []):
        if isinstance(ch, dict):
            walk_tree(ch, chain, sink)


def collect_towns(tree):
    """从下钻响应中收集全部 level==4 节点（含其祖先链）。"""
    out = []

    def sink(node, chain):
        if node.get("level") == 4:
            code = norm_code(4, node.get("code"), node.get("name"))
            if not code:
                return
            out.append({
                "code12": node.get("code"),
                "code": code,
                "name": node.get("name"),
                "type": node.get("type") or "",
                "chain": [dict(x) for x in chain],
            })

    walk_tree(tree.get("data") or {}, [], sink)
    return out


def match_code(rec, code):
    """记录是否属于指定代码：其自身或任一上级代码以 code 开头（支持只填部分代码）。"""
    for key in ("code", "parent_code", "province_code", "city_code", "county_code"):
        v = rec.get(key)
        if v and str(v).startswith(code):
            return True
    return False


# --------------------------------------------------------------------------
# 落盘（四种格式）
# --------------------------------------------------------------------------
def row_csv(r):
    """按 COLS 顺序取 16 个字段值，缺失一律用空串。"""
    return [
        r["code"], r["name"], r["admin_level"], r["level_name"], r["division_type"],
        r["parent_code"] or "", r["parent_name"] or "",
        r["province_code"] or "", r["province_name"] or "",
        r["city_code"] or "", r["city_name"] or "",
        r["county_code"] or "", r["county_name"] or "",
        r["full_name"] or "", "true" if r["is_leaf"] else "false", FETCH_DATE,
    ]


def row_json(r):
    """字段名与 CSV 表头一致；缺失用空串，is_leaf 用布尔。"""
    d = dict(zip(COLS, row_csv(r)))
    d["is_leaf"] = bool(r["is_leaf"])
    return d


def write_csv(path, recs):
    with open(path, "w", encoding="utf-8-sig", newline="") as f:
        w = csv.writer(f)
        w.writerow(COLS)
        for r in recs:
            w.writerow(row_csv(r))


def write_txt(path, recs):
    """制表符分隔，UTF-8 带 BOM（Excel / 记事本双击不乱码）。"""
    with open(path, "w", encoding="utf-8-sig", newline="") as f:
        f.write("\t".join(COLS) + "\n")
        for r in recs:
            f.write("\t".join(str(x) for x in row_csv(r)) + "\n")


def write_json(path, recs):
    """JSON 数组，每个元素一行，UTF-8 无 BOM（保证任何解析器都能读）。"""
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        if not recs:
            f.write("[]\n")
            return
        f.write("[\n")
        f.write(",\n".join("  " + json.dumps(row_json(r), ensure_ascii=False) for r in recs))
        f.write("\n]\n")


def _esc(s):
    if s is None or s == "":
        return "NULL"
    return "'" + str(s).replace("'", "''") + "'"


def write_sql(path, recs, stat):
    """写出建表与 INSERT 语句。

    口径：主体为通用 SQL（CREATE TABLE / INSERT INTO 皆标准写法，不依赖任何方言），
    落地上按 **MySQL 5.7+ / 8.0 可直接执行**为标准——列注释用 COMMENT '...'、
    布尔用 TINYINT(1)、表尾带 ENGINE / CHARSET / COLLATE。换库时见文末「其他数据库适配」，
    那几处是仅有的 MySQL 专有写法。
    """
    four = stat.get(4, 0) > 0
    lines = []
    a = lines.append
    a("-- ============================================================")
    a("-- 全国行政区划维度表（%s）" % ("省 / 地 / 县 / 乡 四级" if four else "省 / 地 / 县 三级"))
    a("-- 数据来源：民政部·国家地名信息库 %s" % SRC_URL)
    a("-- 抓取日期：%s" % FETCH_DATE)
    a("-- 记录数：%d（%s）" % (len(recs), " / ".join(
        "%s %d" % (LEVEL_NAME[k], v) for k, v in sorted(stat.items()))))
    a("-- 编码口径：区县及以上 6 位(GB/T 2260)，乡级 9 位(GB/T 10114)")
    a("-- 数据修订：接口未返回台湾省数字编码（原为中文「资料暂缺」），")
    a("--           已按其 GB/T 2260 标准码 710000 补齐，避免非数字内容进入编号列。")
    a("--")
    a("-- 语法口径：通用 SQL 为主，默认按 MySQL 5.7+ / 8.0 可直接执行")
    a("--           （列注释 COMMENT '...'、布尔 TINYINT(1)、表尾 ENGINE / CHARSET / COLLATE）")
    a("-- 用法一（重建）：整体执行本脚本")
    a("-- 用法二（仅刷新）：跳过 DROP/CREATE，先 TRUNCATE TABLE dim_xzqh; 再执行 INSERT 段")
    a("-- ============================================================")
    a("")
    a("-- 显式声明连接字符集，避免中文按 latin1 写入后乱码（MySQL 语句）")
    a("SET NAMES utf8mb4;")
    a("")
    a("DROP TABLE IF EXISTS dim_xzqh;")
    a("")
    a("CREATE TABLE dim_xzqh (")
    a("    code            VARCHAR(9)   NOT NULL COMMENT '行政区划代码；区县及以上 6 位(GB/T 2260)，乡级 9 位(GB/T 10114)',")
    a("    name            VARCHAR(120) NOT NULL COMMENT '行政区划名称',")
    a("    admin_level     SMALLINT     NOT NULL COMMENT '层级：1=省级 2=地级 3=县级 4=乡级',")
    a("    level_name      VARCHAR(16)  NOT NULL COMMENT '层级名称：省级/地级/县级/乡级',")
    a("    division_type   VARCHAR(32)           COMMENT '区划类型：省/自治区/直辖市/特别行政区/地级市/自治州/地区/盟/市辖区/县/县级市/自治县/旗/街道/镇/乡/民族乡/苏木/区公所等',")
    a("    parent_code     VARCHAR(9)            COMMENT '直接上级代码；省及港澳台为 NULL',")
    a("    parent_name     VARCHAR(120)          COMMENT '直接上级名称',")
    a("    province_code   VARCHAR(6)            COMMENT '所属省级代码',")
    a("    province_name   VARCHAR(120)          COMMENT '所属省级名称',")
    a("    city_code       VARCHAR(6)            COMMENT '所属地级代码；直辖市辖区、省直辖县级、直筒子市下辖乡级为 NULL',")
    a("    city_name       VARCHAR(120)          COMMENT '所属地级名称',")
    a("    county_code     VARCHAR(6)            COMMENT '所属县级代码；直筒子市下辖乡级为 NULL',")
    a("    county_name     VARCHAR(120)          COMMENT '所属县级名称',")
    a("    full_name       VARCHAR(300)          COMMENT '自顶向下全称，如「河北省石家庄市长安区建北街道」',")
    a("    is_leaf         TINYINT(1)   NOT NULL DEFAULT 0 COMMENT '是否叶子节点（无下级数据）：1=是 0=否',")
    a("    fetch_date      DATE                  COMMENT '数据抓取日期',")
    a("    PRIMARY KEY (code)")
    a(") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci")
    a("  COMMENT='全国行政区划维度表（省/地/县/乡）';")
    a("")
    for col in ("parent_code", "province_code", "city_code", "county_code"):
        a("CREATE INDEX idx_dim_xzqh_%s ON dim_xzqh (%s);" % (col.replace("_code", ""), col))
    a("CREATE INDEX idx_dim_xzqh_level ON dim_xzqh (admin_level);")
    a("CREATE INDEX idx_dim_xzqh_name  ON dim_xzqh (name);")
    a("")
    a("-- 其他数据库适配（本文件默认按 MySQL 书写，以下为仅有的几处 MySQL 专有写法）：")
    a("--   · 表尾 ENGINE / DEFAULT CHARSET / COLLATE / COMMENT 与上方 SET NAMES：换库时整段删除；")
    a("--   · 列级 COMMENT '...' 同理，PostgreSQL / Oracle / GaussDB 请改为独立的")
    a("--     COMMENT ON COLUMN dim_xzqh.xxx IS '...'; 语句（原 GaussDB 版即如此）；")
    a("--   · is_leaf 的 TINYINT(1) 在其他库可写 BOOLEAN；")
    a("--   · GaussDB 分布式部署可选：DISTRIBUTE BY HASH(code);")
    a("")

    fields = ", ".join(COLS)
    batch = 500
    tuples = []
    for r in recs:
        tuples.append("(" + ", ".join([
            _esc(r["code"]), _esc(r["name"]), str(r["admin_level"]), _esc(r["level_name"]),
            _esc(r["division_type"]), _esc(r["parent_code"]), _esc(r["parent_name"]),
            _esc(r["province_code"]), _esc(r["province_name"]),
            _esc(r["city_code"]), _esc(r["city_name"]),
            _esc(r["county_code"]), _esc(r["county_name"]),
            _esc(r["full_name"]), "1" if r["is_leaf"] else "0", _esc(FETCH_DATE),
        ]) + ")")
    for i in range(0, len(tuples), batch):
        a("INSERT INTO dim_xzqh (%s) VALUES" % fields)
        a(",\n".join(tuples[i:i + batch]) + ";")
        a("")
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")


# --------------------------------------------------------------------------
# 主流程
# --------------------------------------------------------------------------
def build(out_dir, out_type, code_filter, levels, cache_dir, workers, sleep_s, cache_ttl):
    t_start = time.time()
    cache = Cache(cache_dir, cache_ttl)
    suffix = "_l4" if levels >= 4 else ""

    # ---- 1. 顶层三层（★ 不传 code 参数，否则漏台湾省）----
    step(1, 4, "获取省级 / 地级 / 县级 ...")
    top = cache.get("top3", SRC_URL + "?maxLevel=3")
    root = top.get("data") or {}

    nodes = {}          # code6 -> {code,name,level,type,chain}
    parent_of = {}      # code6 -> parent code6

    def sink3(node, chain):
        lv = node["level"]
        if lv > 3:
            return
        c = norm_code(lv, node.get("code"), node.get("name"))
        if not c:
            return
        nodes[c] = {"code": c, "name": node["name"], "level": lv,
                    "type": node["type"] or "",
                    "chain": [dict(x) for x in chain]}

    walk_tree(root, [], sink3)

    def link(node, pcode):
        lv = node.get("level")
        if lv and lv >= 1:
            cur = norm_code(lv, node.get("code"), node.get("name"))
            if pcode and cur:
                parent_of[cur] = pcode
            pcode = cur or pcode
        for ch in (node.get("children") or []):
            link(ch, pcode)

    link(root, None)

    def inside(sub, parent):
        """sub 是否在 parent 的子树内（沿 parent_of 上溯，含自身）。"""
        cur, guard = sub, 0
        while cur and guard < 12:
            if cur == parent:
                return True
            cur = parent_of.get(cur)
            guard += 1
        return False

    lv_cnt = {}
    for v in nodes.values():
        lv_cnt[v["level"]] = lv_cnt.get(v["level"], 0) + 1
    info("省级 %s / 地级 %s / 县级 %s" % (
        fmt(lv_cnt.get(1, 0)), fmt(lv_cnt.get(2, 0)), fmt(lv_cnt.get(3, 0))))
    if lv_cnt.get(1, 0) != 34:
        err("省级数量异常（预期 34），请检查是否误传了 code= 空参数")

    # ---- 2. 乡级下钻（可只钻目标子树，避免导出单个省也要 451 次请求）----
    towns = []
    if levels >= 4:
        p2 = [v for v in nodes.values() if v["level"] == 2]
        p3_direct = [v for v in nodes.values() if v["level"] == 3
                     and parent_of.get(v["code"]) and nodes.get(parent_of[v["code"]], {}).get("level") == 1]
        roots = [v for v in (p2 + p3_direct) if v["code"].isdigit()]

        if code_filter and code_filter in nodes:
            total_roots = len(roots)
            keep = [v for v in roots
                    if inside(v["code"], code_filter) or inside(code_filter, v["code"])]
            if keep and len(keep) < total_roots:
                roots = keep
                info("按代码 %s 裁剪下钻范围：%d → %d 次请求（跳过 %d 个无关地级 / 县级）"
                     % (code_filter, total_roots, len(roots), total_roots - len(roots)))

        tasks = [(v["code"] + "000000", v["code"]) for v in roots]
        step(2, 4, "下钻乡级：%d 次请求（并发 %d）" % (len(tasks), workers))

        t0 = time.time()
        done = 0
        errors = []

        def job(item):
            code12, code6 = item
            url = SRC_URL + "?" + urllib.parse.urlencode({"code": code12, "maxLevel": 4})
            return code6, cache.get("t_" + code12, url)

        with ThreadPoolExecutor(max_workers=workers) as ex:
            futs = {ex.submit(job, t): t for t in tasks}
            for f in as_completed(futs):
                code6 = futs[f][1]
                done += 1
                try:
                    _, tree = f.result()
                    for g in collect_towns(tree):
                        g["src"] = code6
                        towns.append(g)
                except Exception as e:                               # noqa: BLE001
                    errors.append({"code": code6, "err": str(e)})
                if done % 25 == 0 or done == len(tasks):
                    info("下钻乡级 %s  %d/%d  已获取 %s 条  %.0fs"
                         % (bar(done, len(tasks)), done, len(tasks), fmt(len(towns)), time.time() - t0))
        info("下钻完成 %d 次 / %.1fs / 失败 %d" % (done, time.time() - t0, len(errors)))
        if errors:
            err("%d 个下级区划抓取失败：%s" % (
                len(errors), ", ".join(e["code"] for e in errors[:10])))
    else:
        step(2, 4, "跳过乡级（导出级别 = 省市县区）")

    # ---- 3. 组装 ----
    step(3, 4, "组装与校验 ...")
    uniq = {}
    for g in towns:
        uniq[g["code"]] = g

    def anc_level(code6, want):
        cur, guard = code6, 0
        while cur and guard < 10:
            v = nodes.get(cur)
            if v and v["level"] == want:
                return v
            cur = parent_of.get(cur)
            guard += 1
        return None

    recs = []
    for code6, v in nodes.items():
        chain = v["chain"]
        prov = next((a for a in chain if a["level"] == 1), None)
        city = next((a for a in chain if a["level"] == 2), None)
        cnty = next((a for a in chain if a["level"] == 3), None)
        pc = parent_of.get(code6)
        recs.append({
            "code": code6, "name": v["name"], "admin_level": v["level"],
            "level_name": LEVEL_NAME[v["level"]], "division_type": v["type"],
            "parent_code": pc, "parent_name": nodes.get(pc, {}).get("name"),
            "province_code": prov["code"] if prov else None, "province_name": prov["name"] if prov else None,
            "city_code": city["code"] if city else None, "city_name": city["name"] if city else None,
            "county_code": cnty["code"] if cnty else None, "county_name": cnty["name"] if cnty else None,
            "full_name": "".join(a["name"] for a in chain if a["name"]),
        })

    for g in uniq.values():
        # chain 末项是乡级自己，倒数第二项才是直接上级
        parent = g["chain"][-2]
        pc6 = parent["code"]
        prov, city, cnty = anc_level(pc6, 1), anc_level(pc6, 2), anc_level(pc6, 3)
        pre = "".join(x["name"] for x in (prov, city, cnty) if x and x.get("name"))
        recs.append({
            "code": g["code"], "name": g["name"], "admin_level": 4,
            "level_name": "乡级", "division_type": g["type"],
            "parent_code": pc6, "parent_name": parent["name"],
            "province_code": prov["code"] if prov else None, "province_name": prov["name"] if prov else None,
            "city_code": city["code"] if city else None, "city_name": city["name"] if city else None,
            "county_code": cnty["code"] if cnty else None, "county_name": cnty["name"] if cnty else None,
            "full_name": pre + g["name"],
        })

    parents = {r["parent_code"] for r in recs if r["parent_code"]}
    for r in recs:
        r["is_leaf"] = r["code"] not in parents

    # ---- 3b. 按代码过滤 + 按编码正序排序 ----
    if code_filter:
        before = len(recs)
        recs = [r for r in recs if match_code(r, code_filter)]
        info("按代码 %s 过滤：%s → %s 条" % (code_filter, fmt(before), fmt(len(recs))))
        if not recs:
            err("代码 %s 未匹配到任何行政区划；请确认代码是否正确（如 110000 北京市、110101 东城区）"
                % code_filter)
            return []

    recs.sort(key=lambda r: r["code"])

    stat = {}
    for r in recs:
        stat[r["admin_level"]] = stat.get(r["admin_level"], 0) + 1
    tstat = {}
    for r in recs:
        if r["admin_level"] == 4:
            tstat[r["division_type"]] = tstat.get(r["division_type"], 0) + 1

    known = {r["code"] for r in recs}
    dangling = 0
    for r in recs:
        for k in ("parent_code", "province_code", "city_code", "county_code"):
            v = r[k]
            if v and v not in known:
                dangling += 1
    bad_code = [r["code"] for r in recs if not str(r["code"]).isdigit()]
    info("合计 %s 条  %s" % (
        fmt(len(recs)), " / ".join("%s %s" % (LEVEL_NAME[k], fmt(v)) for k, v in sorted(stat.items()))))
    if tstat:
        info("乡级类型：%s" % " · ".join(
            "%s %s" % (k, fmt(v)) for k, v in sorted(tstat.items(), key=lambda x: -x[1])))
    info("引用悬空 %d 处 | 非数字编码 %d 条%s" % (
        dangling, len(bad_code), "" if not bad_code else " " + ",".join(bad_code[:5])))
    if UNMAPPED:
        err("以下编码非数字且未配置补齐规则（已跳过，请补 CODE_FALLBACK）：")
        for lv, nm, c in UNMAPPED:
            err("  level=%s %s -> %r" % (lv, nm, c))

    # ---- 4. 落盘 ----
    step(4, 4, "写出 %s 文件 ..." % out_type)
    targets = {
        "csv": ("china_xzqh%s.csv" % suffix, write_csv),
        "txt": ("china_xzqh%s.txt" % suffix, write_txt),
        "json": ("china_xzqh%s.json" % suffix, write_json),
        "sql": ("china_xzqh%s.sql" % suffix,
                lambda p, r: write_sql(p, r, stat)),
    }
    name, writer = targets[out_type]
    out_path = os.path.join(out_dir, name)
    writer(out_path, recs)

    print("", flush=True)
    print("=" * 62, flush=True)
    result("导出完成：%s  |  %s 条  |  用时 %.1fs" % (name, fmt(len(recs)), time.time() - t_start))
    result("缓存命中 %d / 超期重抓 %d / 实际请求 %d" % (cache.hits, cache.expired, cache.misses))
    result("输出文件: %s" % out_path)
    print("=" * 62, flush=True)
    return recs


def main():
    _reconfig_stdout()
    ap = argparse.ArgumentParser(description="全国行政区划导出（省 / 地 / 县 / 乡）")
    ap.add_argument("-ExportDir", dest="export_dir", default="_p{EXPORT_DIR}",
                    help="导出目录（必填）")
    ap.add_argument("-Code", dest="code", default="_p{CODE}",
                    help="省市县区代码（可留空 = 导出全部）")
    ap.add_argument("-Level", dest="level", default="_p{LEVEL}",
                    help="导出级别：省市县区 / 乡镇街道")
    ap.add_argument("-Type", dest="type", default="_p{TYPE}",
                    help="导出类型：csv / txt / json / sql")
    ap.add_argument("--cache", default=None,
                    help="缓存目录（默认 %%LOCALAPPDATA%%\\knife-script-manager\\cache\\xzqh）")
    ap.add_argument("--cache-ttl", type=int, default=7,
                    help="缓存有效期（天），默认 7；0 = 永不过期")
    ap.add_argument("--workers", type=int, default=2, help="下钻并发数，默认 2（接口限流，勿调高）")
    ap.add_argument("--sleep", type=float, default=0.18, help="每次请求后的间隔秒数")
    args = ap.parse_args()

    print("=" * 62, flush=True)
    print("中国行政区划", flush=True)
    print("=" * 62, flush=True)
    ut = update_time()
    if ut:
        info("更新时间: %s" % ut)

    export_dir = ("" if is_unset(args.export_dir) else args.export_dir.strip())
    code_raw = ("" if is_unset(args.code) else args.code.strip())
    level_raw = ("" if is_unset(args.level) else args.level.strip())
    type_raw = ("" if is_unset(args.type) else args.type.strip().lower())
    if type_raw not in ("csv", "txt", "json", "sql"):
        type_raw = "csv"
    levels = 4 if "乡镇" in level_raw else 3

    param("导出目录: %s" % (export_dir or "(未填写)"))
    param("省市县区代码: %s" % (code_raw or "(空 = 导出全部)"))
    param("导出级别: %s → %s" % (level_raw or "省市县区",
                                "含乡镇街道（四级）" if levels == 4 else "省 / 地 / 县（三级）"))
    param("导出类型: %s" % type_raw)

    if not export_dir:
        err("未提供导出目录（参数 -ExportDir），请在界面上选择导出目录后重试")
        return 1
    if os.path.exists(export_dir) and not os.path.isdir(export_dir):
        err("导出目录指向了一个文件，请选择目录: %s" % export_dir)
        return 1
    try:
        os.makedirs(export_dir, exist_ok=True)
    except OSError as e:
        err("导出目录不可用: %s —— %s" % (export_dir, e))
        return 1

    cache_dir = os.path.abspath(args.cache) if args.cache else default_cache_dir()
    info("缓存目录: %s" % cache_dir)

    recs = build(export_dir, type_raw, code_raw, levels, cache_dir,
                 args.workers, args.sleep, args.cache_ttl)
    if not recs:
        err("未导出任何记录，未生成文件")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
