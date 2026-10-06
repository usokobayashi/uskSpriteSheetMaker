//==================================================
// MapDocument
// マップの配置・画像参照・補正・アニメーションをプロジェクト内に保持する。
//==================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace SpriteSheetMaker
{
    [DataContract]
    internal sealed class MapAsset
    {
        [DataMember] public string Id = Guid.NewGuid().ToString("N");
        [DataMember] public string Path = "";
        [DataMember] public int Width = 8;
        [DataMember] public int Height = 8;
        [DataMember] public int Cell;
        [DataMember] public bool Available = true;
        [DataMember] public int CorrectedWidth;
        [DataMember] public int CorrectedHeight;
        [DataMember] public bool Ignored;
        public Size Size { get { return new Size(CorrectedWidth > 0 ? CorrectedWidth : Width, CorrectedHeight > 0 ? CorrectedHeight : Height); } }
    }
    [DataContract]
    internal sealed class MapAnimation
    {
        [DataMember] public string Id = Guid.NewGuid().ToString("N");
        [DataMember] public string Name = "";
        [DataMember] public List<string> Frames = new List<string>();
        [DataMember] public int Fps = 8;
        [DataMember] public bool Enabled = true;
    }
    [DataContract]
    internal sealed class MapPlacement
    {
        [DataMember] public int X;
        [DataMember] public int Y;
        [DataMember] public int Layer;
        [DataMember] public string Source = "";
        [DataMember] public bool Animated;
        [DataMember] public int Rotation;   // 時計回りの90°単位（0〜3）
        [DataMember] public bool FlipX;     // 左右反転（回転より先に掛ける）
        [DataMember] public bool FlipY;     // 上下反転（回転より先に掛ける）
    }
    [DataContract]
    internal sealed class MapDocument
    {
        public const int Extent = 20;
        public static readonly double[] Ratios = { .25, .5, 1, 2, 3, 4, 5, 6, 7, 8 };
        [DataMember] public string BasisId = "";
        [DataMember] public int CellWidth = 8;
        [DataMember] public int CellHeight = 8;
        [DataMember] public int ActiveLayer;
        [DataMember] public bool[] VisibleLayers = { true, true, true, true };
        [DataMember] public List<MapAsset> Assets = new List<MapAsset>();
        [DataMember] public List<MapAnimation> Animations = new List<MapAnimation>();
        [DataMember] public List<MapPlacement> Tiles = new List<MapPlacement>();
        // シート上の並び（行ごとの画像ID）。フォルダ一覧の順は変えず、表示と書き出しの配置だけをここで持つ。
        // 各行は左詰め、行は上から詰めて置く（意図した空白は作れない）。横長・縦長は行の分け方で自由に決まる。
        [DataMember] public List<List<string>> Rows = new List<List<string>>();
        // 旧形式の並び（1列の順番）。読み込み時の初期並びにだけ使う。
        [DataMember] public List<string> Order = new List<string>();

        public MapAsset Asset(string id) { return Assets.FirstOrDefault(a => a.Id == id); }
        public bool SetTile(int x, int y, string source, bool animated, bool erase)
        {
            if (x < 0 || y < 0 || x >= Extent || y >= Extent || ActiveLayer < 0 || ActiveLayer >= 4) return false;
            MapPlacement old = Tiles.FirstOrDefault(t => t.X == x && t.Y == y && t.Layer == ActiveLayer);
            if (erase) return old != null && Tiles.Remove(old);
            if (string.IsNullOrEmpty(source) || (animated ? !Animations.Any(a => a.Id == source) : Asset(source) == null)) return false;
            if (old != null && old.Source == source && old.Animated == animated) return false;
            if (old != null) Tiles.Remove(old);
            Tiles.Add(new MapPlacement { X = x, Y = y, Layer = ActiveLayer, Source = source, Animated = animated });
            return true;
        }
        // 置いたチップが占める大きさ（px）。90°・270°回転では縦横が入れ替わる。アニメーションは1コマ目の大きさ。
        public Size FootprintOf(MapPlacement tile)
        {
            string id = tile.Animated ? (Animations.FirstOrDefault(a => a.Id == tile.Source) ?? new MapAnimation()).Frames.FirstOrDefault() : tile.Source;
            MapAsset asset = Asset(id ?? "");
            Size size = asset == null ? new Size(CellWidth, CellHeight) : asset.Size;
            return tile.Rotation % 2 == 1 ? new Size(size.Height, size.Width) : size;
        }
        // layer でマス (x, y) を覆っているチップ（大きなチップは覆うどのマスでも当たる）。後から置いたものを優先。
        public MapPlacement TileAt(int layer, int x, int y)
        {
            for (int i = Tiles.Count - 1; i >= 0; i--)
            {
                MapPlacement t = Tiles[i];
                if (t.Layer != layer) continue;
                Size f = FootprintOf(t);
                int w = Math.Max(1, (f.Width + CellWidth - 1) / CellWidth), h = Math.Max(1, (f.Height + CellHeight - 1) / CellHeight);
                if (x >= t.X && x < t.X + w && y >= t.Y && y < t.Y + h) return t;
            }
            return null;
        }
        public bool EraseAt(int x, int y)
        {
            MapPlacement t = TileAt(ActiveLayer, x, y);
            return t != null && Tiles.Remove(t);
        }
        // チップを別のマスへ動かす。移動先の同じレイヤー・同じマスにあったチップは置き換える。
        public bool MoveTile(MapPlacement tile, int x, int y)
        {
            if (tile == null || x < 0 || y < 0 || x >= Extent || y >= Extent || (tile.X == x && tile.Y == y)) return false;
            Tiles.RemoveAll(t => t != tile && t.Layer == tile.Layer && t.X == x && t.Y == y);
            tile.X = x; tile.Y = y; return true;
        }

        public void SetBasis(MapAsset asset)
        {
            if (asset == null || !asset.Available) return;
            BasisId = asset.Id; CellWidth = asset.Width; CellHeight = asset.Height;
        }
        public static int NearestDimension(int pixels, int basis)
        {
            return Ratios.Select(r => Math.Max(1, (int)Math.Round(basis * r, MidpointRounding.AwayFromZero)))
                .Distinct().OrderBy(n => Math.Abs(n - pixels)).ThenBy(n => n).First();
        }
        public bool NeedsNormalization(MapAsset a)
        {
            return a.Available && !a.Ignored && (a.Size.Width != NearestDimension(a.Size.Width, CellWidth) || a.Size.Height != NearestDimension(a.Size.Height, CellHeight));
        }
        public void Normalize(MapAsset a)
        {
            a.CorrectedWidth = NearestDimension(a.Width, CellWidth);
            a.CorrectedHeight = NearestDimension(a.Height, CellHeight);
            a.Ignored = false;
        }
        public string FrameId(string source, bool animated, double seconds)
        {
            if (!animated) return source;
            MapAnimation clip = Animations.FirstOrDefault(a => a.Id == source);
            if (clip == null || clip.Frames.Count == 0) return "";
            int index = clip.Enabled ? (int)(Math.Max(0, seconds) * Math.Max(1, clip.Fps) % clip.Frames.Count) : 0;
            return clip.Frames[index];
        }
        // 並べるときにまとめるアニメーション（定義の一覧で先にあるもの）。
        public MapAnimation GroupOf(string assetId)
        {
            if (string.IsNullOrEmpty(assetId)) return null;
            return Animations.FirstOrDefault(a => a.Frames.Contains(assetId));
        }
        // id から始まるまとまり。アニメーションのコマは、指定しやすいよう必ずコマ順に左から右へ続けて並べる
        // （最初に出てくるコマの位置に、まとまりとして入る）。
        private List<string> UnitAt(string id, HashSet<string> placed)
        {
            MapAsset asset = Asset(id);
            if (asset == null || !asset.Available || placed.Contains(id)) return null;
            MapAnimation clip = GroupOf(id);
            List<string> unit = clip == null ? new List<string> { id }
                : clip.Frames.Distinct().Where(f => !placed.Contains(f) && GroupOf(f) == clip && Asset(f) != null && Asset(f).Available).ToList();
            if (unit.Count == 0) unit.Add(id);
            foreach (string f in unit) placed.Add(f);
            return unit;
        }
        // 1行の幅の上限（px）。これを超える分は次の行へ送る。壊れた・悪意のあるファイルで、幅の合計が
        // 整数の上限を超えて落ちたり、詰める計算の配列が巨大になってメモリを使い切ったりしないため。
        public const int MaxRowWidth = 1 << 20;
        // 上限より広いまとまりを、上限に収まる長さごとに分ける（ふつうは分けない）。
        private List<List<string>> SplitWide(List<string> unit)
        {
            var parts = new List<List<string>>(); var part = new List<string>(); long used = 0;
            foreach (string id in unit)
            {
                MapAsset a = Asset(id); int w = a == null ? 0 : a.Size.Width;
                if (part.Count > 0 && used + w > MaxRowWidth) { parts.Add(part); part = new List<string>(); used = 0; }
                part.Add(id); used += w;
            }
            if (part.Count > 0) parts.Add(part);
            return parts;
        }
        // コマを横一列に並べたときに上限に収まるか（アニメーションを作るときに確かめる）。
        public bool FitsInRow(IEnumerable<string> frames) { return frames.Distinct().Select(Asset).Where(a => a != null).Sum(a => (long)a.Size.Width) <= MaxRowWidth; }
        public Size UnitSize(List<string> unit)
        {
            List<MapAsset> assets = unit.Select(Asset).Where(a => a != null).ToList();
            long width = assets.Sum(a => (long)a.Size.Width);
            return new Size((int)Math.Min(MaxRowWidth, width), assets.Select(a => a.Size.Height).DefaultIfEmpty(0).Max());
        }
        private int RowWidth(List<List<string>> row) { return (int)Math.Min(MaxRowWidth, row.Sum(u => (long)UnitSize(u).Width)); }
        // 行 → まとまり → 画像ID。並びがまだない画像（初めて開いたとき・後から足した画像）は後ろへ足す。
        // 横セル数は、並びがまだ1行もないときの折り返し幅にだけ使う（以後は一番広い行の幅で折り返す）。
        public List<List<List<string>>> LayoutRows(int initialColumns)
        {
            var placed = new HashSet<string>();
            var rows = new List<List<List<string>>>();
            foreach (List<string> saved in Rows)
            {
                if (saved == null) continue;
                var row = new List<List<string>>();
                foreach (string id in saved) { List<string> unit = UnitAt(id, placed); if (unit != null) row.Add(unit); }
                if (row.Count > 0) rows.Add(row);
            }
            var index = new Dictionary<string, int>();
            for (int i = 0; i < Order.Count; i++) if (Order[i] != null && !index.ContainsKey(Order[i])) index[Order[i]] = i;
            int position;
            List<string> rest = Assets.Where(a => a.Available && !placed.Contains(a.Id))
                .OrderBy(a => index.TryGetValue(a.Id, out position) ? position : int.MaxValue).ThenBy(a => a.Cell).Select(a => a.Id).ToList();
            int limit = rows.Count == 0 ? CellWidth * Math.Max(1, initialColumns) : Math.Max(CellWidth, rows.Max(r => RowWidth(r)));
            foreach (string id in rest)
            {
                List<string> unit = UnitAt(id, placed); if (unit == null) continue;
                if (rows.Count == 0 || (rows[rows.Count - 1].Count > 0 && RowWidth(rows[rows.Count - 1]) + UnitSize(unit).Width > limit)) rows.Add(new List<List<string>>());
                rows[rows.Count - 1].Add(unit);
            }
            return rows;
        }
        // 今の並びを確定して保存する（以後、横セル数を変えても並びは変わらない）。
        public void FixLayout(int initialColumns) { SetRows(LayoutRows(initialColumns)); }
        public void SetRows(List<List<List<string>>> rows)
        { Rows = rows.Where(r => r.Count > 0).Select(r => r.SelectMany(u => u).ToList()).ToList(); }
        public static string RowsKey(List<List<List<string>>> rows)
        { return string.Join("|", rows.Select(r => string.Join(",", r.SelectMany(u => u)))); }
        // シート上のセル番号（読み順: 上から、同じ高さなら左から 1, 2, 3…）。UV の確認や番号の入力に使い、並び替えると振り直される。
        public Dictionary<string, int> NumbersOf(List<List<List<string>>> rows) { return NumbersOf(PackRows(rows)); }
        public static Dictionary<string, int> NumbersOf(Dictionary<string, Rectangle> packing)
        {
            var numbers = new Dictionary<string, int>(); int n = 0;
            foreach (var entry in packing.OrderBy(p => p.Value.Y).ThenBy(p => p.Value.X)) numbers[entry.Key] = ++n;
            return numbers;
        }
        public Dictionary<string, int> SheetNumbers(int initialColumns) { return NumbersOf(Pack(initialColumns)); }
        public MapAsset AssetAtNumber(int number, int initialColumns)
        {
            string id = SheetNumbers(initialColumns).Where(p => p.Value == number).Select(p => p.Key).FirstOrDefault();
            return id == null ? null : Asset(id);
        }
        public List<MapAsset> PaletteOrder(int initialColumns = 8) { return LayoutRows(initialColumns).SelectMany(r => r).SelectMany(u => u).Select(Asset).ToList(); }
        // 置ける範囲: すべてのチップを横一列に並べた幅 × 縦一列に積んだ高さ。
        public Size RangeSize()
        {
            List<MapAsset> live = Assets.Where(a => a.Available).ToList();
            return new Size((int)Math.Min(MaxRowWidth, live.Sum(a => (long)a.Size.Width)), (int)Math.Min(int.MaxValue / 4, live.Sum(a => (long)a.Size.Height)));
        }
        // id を含むまとまりを、row 行目の position 番目へ移す（row が行数と同じなら新しい行を下に作る）。
        public bool MoveUnit(string id, int row, int position, int initialColumns = 8)
        {
            List<List<List<string>>> rows = LayoutRows(initialColumns);
            string before = RowsKey(rows);
            List<string> unit = null;
            foreach (List<List<string>> r in rows) { int i = r.FindIndex(u => u.Contains(id)); if (i >= 0) { unit = r[i]; r.RemoveAt(i); break; } }
            if (unit == null) return false;
            rows.RemoveAll(r => r.Count == 0);
            row = Math.Max(0, Math.Min(rows.Count, row));
            if (row == rows.Count) rows.Add(new List<List<string>> { unit });
            else rows[row].Insert(Math.Max(0, Math.Min(rows[row].Count, position)), unit);
            if (RowsKey(rows) == before) return false;
            SetRows(rows); return true;
        }
        // 画像がコマとして使われているアニメーション（再生中の定義を優先）。
        public MapAnimation AnimationOf(string assetId)
        {
            if (string.IsNullOrEmpty(assetId)) return null;
            return Animations.Where(a => a.Frames.Contains(assetId)).OrderBy(a => a.Enabled ? 0 : 1).FirstOrDefault();
        }
        public Dictionary<string, Rectangle> Pack(int initialColumns) { return PackRows(LayoutRows(initialColumns)); }
        // 行ごとに左詰めで並べ、各まとまりは上に空きがあれば詰めて置く（その横幅で、すでに置いたチップのすぐ下）。
        // 行の高さを一番高いチップで区切らないので、大きなチップの横にできる空きへ、後の行のチップが入る。
        // ただし前の行の一番早く終わるチップの下端より上へは上がらない（短い行の右の空きへ次の行が上がって行が崩れないように）。
        // アニメーションのまとまりは横一列のまま（まとまり全体で一番低い所にそろえる）。
        public Dictionary<string, Rectangle> PackRows(List<List<List<string>>> rows)
        {
            var result = new Dictionary<string, Rectangle>();
            // 1行の幅が上限を超える分は、次の行として扱う。
            var lines = new List<List<List<string>>>();
            foreach (List<List<string>> row in rows)
            {
                var line = new List<List<string>>(); long used = 0;
                foreach (List<string> whole in row)
                    // 1つで上限より広いまとまり（アニメーション）は、重ならないよう上限ごとに行を分ける（作るときは断る）。
                    foreach (List<string> unit in SplitWide(whole))
                    {
                        int w = UnitSize(unit).Width;
                        if (line.Count > 0 && used + w > MaxRowWidth) { lines.Add(line); line = new List<List<string>>(); used = 0; }
                        line.Add(unit); used += w;
                    }
                lines.Add(line);
            }
            rows = lines;
            int width = 0;
            foreach (List<List<string>> row in rows) width = Math.Max(width, (int)Math.Min(MaxRowWidth, row.Sum(u => (long)UnitSize(u).Width)));
            var sky = new int[Math.Max(1, width)];   // 横位置ごとの、置いたチップの下端
            int floor = 0;   // この行が上がれる限界: 前の行で一番早く終わるチップの下端（行を分けた意味を保つ）
            foreach (List<List<string>> row in rows)
            {
                int x = 0, rowFloor = int.MaxValue;
                foreach (List<string> unit in row)
                {
                    List<MapAsset> assets = unit.Select(Asset).Where(a => a != null).ToList();
                    int unitWidth = UnitSize(unit).Width, y = floor;
                    for (int i = x; i < x + unitWidth && i < sky.Length; i++) y = Math.Max(y, sky[i]);
                    foreach (MapAsset a in assets)
                    {
                        result[a.Id] = new Rectangle(x, y, a.Size.Width, a.Size.Height);
                        int bottom = (int)Math.Min(int.MaxValue / 4, (long)y + a.Size.Height);   // 縦も桁あふれさせない
                        for (int i = x; i < x + a.Size.Width && i < sky.Length; i++) sky[i] = bottom;
                        rowFloor = Math.Min(rowFloor, bottom);
                        x = (int)Math.Min(MaxRowWidth, (long)x + a.Size.Width);
                    }
                }
                if (rowFloor != int.MaxValue) floor = rowFloor;
            }
            return result;
        }
        public string ToJson()
        {
            using (var memory = new MemoryStream())
            { new DataContractJsonSerializer(typeof(MapDocument)).WriteObject(memory, this); return Encoding.UTF8.GetString(memory.ToArray()); }
        }
        public static MapDocument FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return new MapDocument();
            MapDocument map;
            using (var memory = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                map = (MapDocument)new DataContractJsonSerializer(typeof(MapDocument)).ReadObject(memory);
            // "map":"null" のような壊れた・悪意のあるデータ。読み込みの形式エラーとして断る（Codex 監査 2026-10-06）。
            if (map == null) throw new System.Runtime.Serialization.SerializationException("map is null");
            map.Assets = map.Assets ?? new List<MapAsset>(); map.Animations = map.Animations ?? new List<MapAnimation>(); map.Tiles = map.Tiles ?? new List<MapPlacement>(); map.Order = map.Order ?? new List<string>(); map.Rows = (map.Rows ?? new List<List<string>>()).Where(r => r != null).ToList();
            map.CellWidth = Math.Max(1, Math.Min(32768, map.CellWidth)); map.CellHeight = Math.Max(1, Math.Min(32768, map.CellHeight));
            map.ActiveLayer = Math.Max(0, Math.Min(3, map.ActiveLayer));
            if (map.VisibleLayers == null || map.VisibleLayers.Length != 4) map.VisibleLayers = new[] { true, true, true, true };
            map.Assets = map.Assets.Where(a => a != null && !string.IsNullOrEmpty(a.Id)).GroupBy(a => a.Id).Select(g => g.First()).ToList();
            map.BasisId = map.BasisId ?? "";
            foreach (MapAsset a in map.Assets) a.Path = a.Path ?? "";
            foreach (MapAsset a in map.Assets)
            { a.Width = Math.Max(1, Math.Min(32768, a.Width)); a.Height = Math.Max(1, Math.Min(32768, a.Height)); a.CorrectedWidth = Math.Max(0, Math.Min(32768, a.CorrectedWidth)); a.CorrectedHeight = Math.Max(0, Math.Min(32768, a.CorrectedHeight)); }
            map.Animations = map.Animations.Where(a => a != null && !string.IsNullOrEmpty(a.Id)).GroupBy(a => a.Id).Select(g => g.First()).ToList();
            // 再生のオン・オフはなくした（常に再生）。
            foreach (MapAnimation a in map.Animations) { a.Frames = a.Frames ?? new List<string>(); a.Fps = Math.Max(1, Math.Min(60, a.Fps)); a.Enabled = true; }
            foreach (MapPlacement t in map.Tiles.Where(t => t != null)) t.Rotation = ((t.Rotation % 4) + 4) % 4;
            map.Tiles = map.Tiles.Where(t => t != null && t.X >= 0 && t.X < Extent && t.Y >= 0 && t.Y < Extent && t.Layer >= 0 && t.Layer < 4)
                .GroupBy(t => t.Layer + ":" + t.X + ":" + t.Y).Select(g => g.Last()).ToList();
            return map;
        }
    }
}
