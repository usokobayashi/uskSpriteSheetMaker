using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace SpriteSheetMaker
{
    // プロジェクトファイル(.smproj)の中身。ZIPの `project.json` として保存する。
    // UI側の状態(AppStateSnapshot)とは独立した、ファイル形式としての定義。
    // 項目を足すときは既定値を持たせ、古いファイルでも読めるようにする。
    [DataContract]
    internal sealed class ProjectDocument
    {
        [DataMember(Name = "formatVersion")] public int FormatVersion = ProjectFile.CurrentFormatVersion;
        [DataMember(Name = "appVersion")] public string AppVersion = "";

        [DataMember(Name = "nextFolderNumber")] public int NextFolderNumber = 1;
        [DataMember(Name = "columns")] public int Columns = 8;
        [DataMember(Name = "scaleIndex")] public int ScaleIndex;
        [DataMember(Name = "fps")] public int Fps = 12;
        [DataMember(Name = "startCell")] public int StartCell = 1;
        [DataMember(Name = "endCell")] public int EndCell = 1;
        [DataMember(Name = "exportNumbers")] public bool ExportNumbers;
        [DataMember(Name = "showAxisNumbers")] public bool ShowAxisNumbers;
        // 見本地形は既定で有効。古いファイルにこの項目が無くても有効のまま読めるよう、反転して持つ。
        [DataMember(Name = "disableTerrain")] public bool DisableTerrain;
        [DataMember(Name = "previewMode")] public string PreviewMode = "";

        [DataMember(Name = "playerMoveSpeed")] public decimal PlayerMoveSpeed = 1;
        [DataMember(Name = "playerJump")] public decimal PlayerJump = 1;
        [DataMember(Name = "playerGravity")] public decimal PlayerGravity = 1;
        [DataMember(Name = "playerGroundOffset")] public decimal PlayerGroundOffset;
        // コライダーの大きさ（画像ピクセル、0 は自動）と表示。古いファイルに無ければ 0・非表示のまま。
        [DataMember(Name = "playerColliderWidth")] public decimal PlayerColliderWidth;
        [DataMember(Name = "playerColliderHeight")] public decimal PlayerColliderHeight;
        [DataMember(Name = "showCollider")] public bool ShowCollider;
        [DataMember(Name = "mirrorMissingDirections")] public bool MirrorMissingDirections;

        [DataMember(Name = "backgroundPalette")] public int BackgroundPalette;
        [DataMember(Name = "blackTransparency")] public bool BlackTransparency;
        [DataMember(Name = "colorBlendMode")] public string ColorBlendMode = "";
        [DataMember(Name = "adjustmentColorArgb")] public int AdjustmentColorArgb = -1;
        [DataMember(Name = "adjustmentStrength")] public int AdjustmentStrength = 100;

        [DataMember(Name = "effectDirectionX")] public decimal EffectDirectionX;
        [DataMember(Name = "effectDirectionY")] public decimal EffectDirectionY;
        [DataMember(Name = "effectSpeed")] public decimal EffectSpeed = 1;
        [DataMember(Name = "effectClip")] public ProjectClip EffectClip = new ProjectClip();
        [DataMember(Name = "simulationRangesInitialized")] public bool SimulationRangesInitialized;

        [DataMember(Name = "playerClips")] public List<ProjectClip> PlayerClips = new List<ProjectClip>();
        [DataMember(Name = "keys")] public List<ProjectKey> Keys = new List<ProjectKey>();
        [DataMember(Name = "folders")] public List<ProjectFolder> Folders = new List<ProjectFolder>();
        [DataMember(Name = "memos")] public List<ProjectMemo> Memos = new List<ProjectMemo>();

        // シートとプレビューの境界位置（シート側の幅の割合 0〜1）。0以下は未指定。
        [DataMember(Name = "sheetPaneRatio")] public double SheetPaneRatio;
    }

    [DataContract]
    internal sealed class ProjectClip
    {
        [DataMember(Name = "state")] public string State = "";
        [DataMember(Name = "enabled")] public bool Enabled;
        [DataMember(Name = "startCell")] public int StartCell = 1;
        [DataMember(Name = "endCell")] public int EndCell = 1;
        [DataMember(Name = "fps")] public int Fps = 12;
    }

    [DataContract]
    internal sealed class ProjectKey
    {
        [DataMember(Name = "action")] public string Action = "";
        [DataMember(Name = "key")] public string Key = "";
    }

    [DataContract]
    internal sealed class ProjectMemo
    {
        [DataMember(Name = "x")] public float X;
        [DataMember(Name = "y")] public float Y;
        [DataMember(Name = "width")] public float Width = 80;
        [DataMember(Name = "text")] public string Text = "";
    }

    [DataContract]
    internal sealed class ProjectFolder
    {
        [DataMember(Name = "name")] public string Name = "";
        [DataMember(Name = "images")] public List<ProjectImage> Images = new List<ProjectImage>();
    }

    [DataContract]
    internal sealed class ProjectImage
    {
        // 元のファイル名（表示に使う）。ZIP内のパスは `images/<フォルダ番号>/<ファイル名>`。
        [DataMember(Name = "name")] public string Name = "";

        // 保存時: 画像の読み込み元パス。読み込み時: 展開先パス。ファイルには書かない。
        public string Path = "";
    }

    internal sealed class ProjectSaveResult
    {
        public readonly List<string> MissingImages = new List<string>();
        public int SavedImages;
    }

    internal sealed class ProjectLoadResult
    {
        public ProjectDocument Document;
        public bool NewerFormat;
        public readonly List<string> MissingImages = new List<string>();
    }

    // 読めないファイル（壊れている・プロジェクトではない）を表す。
    internal sealed class ProjectFormatException : Exception
    {
        public ProjectFormatException(string message, Exception inner) : base(message, inner) { }
    }

    internal static class ProjectFile
    {
        // 2: ジャンプ力の基準を変更（設定値 1.0 = 従来の 1.6）。1 の保存ファイルは読み込み時に換算する。
        public const int CurrentFormatVersion = 2;
        private const decimal LegacyJumpScale = 1.6m;
        public const string Extension = ".smproj";
        private const string DocumentEntry = "project.json";
        private const string ImagesPrefix = "images/";

        // 悪意のある・壊れたファイルでメモリやディスクを使い切られないための上限。
        public const long MaxDocumentBytes = 16L * 1024 * 1024;       // project.json（展開後）
        public const long MaxImageBytes = 1024L * 1024 * 1024;        // 画像1枚
        public const long MaxTotalImageBytes = 8L * 1024 * 1024 * 1024; // 画像の合計
        public const int MaxFolders = 10000;
        public const int MaxImages = 100000;

        // 一時ファイルへ書いてから置き換える。途中で失敗しても元のファイルは壊れない。
        // 読み込み元パスが存在しない画像は含めず、結果へ記録する。
        public static ProjectSaveResult Save(string path, ProjectDocument document)
        {
            var result = new ProjectSaveResult();
            string temporary = path + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, false, Encoding.UTF8))
                {
                    ProjectDocument copy = CloneWithExistingImages(document, archive, result);
                    ZipArchiveEntry entry = archive.CreateEntry(DocumentEntry, CompressionLevel.Optimal);
                    using (Stream entryStream = entry.Open()) Serialize(copy, entryStream);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            catch
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
                throw;
            }
            return result;
        }

        private static ProjectDocument CloneWithExistingImages(ProjectDocument source, ZipArchive archive, ProjectSaveResult result)
        {
            ProjectDocument copy = Deserialize(SerializeToBytes(source));
            copy.FormatVersion = CurrentFormatVersion;
            for (int folderIndex = 0; folderIndex < copy.Folders.Count; folderIndex++)
            {
                ProjectFolder folder = copy.Folders[folderIndex];
                ProjectFolder original = source.Folders[folderIndex];
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                folder.Images.Clear();
                foreach (ProjectImage image in original.Images)
                {
                    if (!File.Exists(image.Path))
                    {
                        result.MissingImages.Add(image.Path);
                        continue;
                    }
                    string name = UniqueName(SafeFileName(System.IO.Path.GetFileName(image.Path)), usedNames);
                    string entryName = ImagesPrefix + folderIndex.ToString("000") + "/" + name;
                    ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
                    using (Stream input = new FileStream(image.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (Stream output = entry.Open())
                        input.CopyTo(output);
                    folder.Images.Add(new ProjectImage { Name = name });
                    result.SavedImages++;
                }
            }
            if (result.MissingImages.Count > 0) RemapCellsAfterRemovingImages(source, copy);
            return copy;
        }

        // 見つからない画像を除くと、後ろの画像のセル番号が詰まる。状態への割り当て（開始〜終了）が
        // 同じ画像を指し続けるよう、残った画像の新しいセル番号へ付け替える（画面の RemapCellAssignments と同じ考え方）。
        // セル番号は、フォルダごとに新しい行から始まり、1行に横セル数ぶんの番号が並ぶ。
        internal static void RemapCellsAfterRemovingImages(ProjectDocument source, ProjectDocument copy)
        {
            int columns = Math.Max(1, source.Columns);
            var oldToNew = new SortedDictionary<int, int>();   // 残った画像の 元のセル番号 → 新しいセル番号
            int nextOld = 1, nextNew = 1;
            for (int folderIndex = 0; folderIndex < source.Folders.Count; folderIndex++)
            {
                List<ProjectImage> images = source.Folders[folderIndex].Images;
                int kept = 0;
                for (int i = 0; i < images.Count; i++)
                {
                    if (!File.Exists(images[i].Path)) continue;
                    oldToNew[nextOld + i] = nextNew + kept;
                    kept++;
                }
                nextOld += (int)Math.Ceiling(images.Count / (double)columns) * columns;
                nextNew += (int)Math.Ceiling(kept / (double)columns) * columns;
            }

            Func<int, int, int[]> remap = (start, end) =>
            {
                List<int> inside = oldToNew.Where(pair => pair.Key >= start && pair.Key <= end).Select(pair => pair.Value).ToList();
                return inside.Count == 0 ? null : new[] { inside.Min(), inside.Max() };
            };
            foreach (ProjectClip clip in copy.PlayerClips.Concat(new[] { copy.EffectClip }))
            {
                if (clip == null) continue;
                int[] range = remap(clip.StartCell, clip.EndCell);
                if (range == null) continue;
                clip.StartCell = range[0];
                clip.EndCell = range[1];
            }
            int[] standard = remap(copy.StartCell, copy.EndCell);
            if (standard != null)
            {
                copy.StartCell = standard[0];
                copy.EndCell = standard[1];
            }
        }

        // 画像を extractDir へ展開して読み込む。ZIP内の不正なパス（親フォルダへ出るもの）は無視する。
        public static ProjectLoadResult Load(string path, string extractDir)
        {
            var result = new ProjectLoadResult();
            try
            {
                using (var archive = ZipFile.OpenRead(path))
                {
                    ZipArchiveEntry documentEntry = archive.GetEntry(DocumentEntry);
                    if (documentEntry == null)
                        throw new ProjectFormatException("project.json not found", null);
                    if (documentEntry.Length > MaxDocumentBytes)
                        throw new ProjectFormatException("project document is too large", null);
                    using (Stream stream = documentEntry.Open())
                        result.Document = Deserialize(ReadAll(stream, MaxDocumentBytes));
                    if (result.Document == null || result.Document.FormatVersion < 1)
                        throw new ProjectFormatException("invalid project document", null);
                    if (result.Document.Folders.Count > MaxFolders ||
                        result.Document.Folders.Sum(folder => folder.Images.Count) > MaxImages)
                        throw new ProjectFormatException("too many folders or images", null);
                    result.NewerFormat = result.Document.FormatVersion > CurrentFormatVersion;
                    if (result.Document.FormatVersion < 2)
                    {
                        // 旧版で保存したジャンプ力は、新しい基準の値へ換算して、跳ぶ高さを保つ。
                        result.Document.PlayerJump = Math.Max(0.1m, Math.Min(10m, Math.Round(result.Document.PlayerJump / LegacyJumpScale, 1)));
                        result.Document.FormatVersion = CurrentFormatVersion;
                    }

                    string root = System.IO.Path.GetFullPath(extractDir).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
                    Directory.CreateDirectory(extractDir);
                    long totalBytes = 0;
                    for (int folderIndex = 0; folderIndex < result.Document.Folders.Count; folderIndex++)
                    {
                        foreach (ProjectImage image in result.Document.Folders[folderIndex].Images)
                        {
                            string entryName = ImagesPrefix + folderIndex.ToString("000") + "/" + image.Name;
                            ZipArchiveEntry entry = archive.GetEntry(entryName);
                            string target = System.IO.Path.GetFullPath(System.IO.Path.Combine(root,
                                folderIndex.ToString("000"), SafeFileName(image.Name)));
                            if (entry == null || !target.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                                entry.Length > MaxImageBytes || totalBytes + entry.Length > MaxTotalImageBytes)
                            {
                                result.MissingImages.Add(image.Name);
                                image.Path = "";
                                continue;
                            }
                            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                            using (Stream input = entry.Open())
                            using (var output = new FileStream(target, FileMode.Create, FileAccess.Write))
                                totalBytes += CopyBounded(input, output, MaxImageBytes);
                            image.Path = target;
                        }
                        result.Document.Folders[folderIndex].Images.RemoveAll(i => string.IsNullOrEmpty(i.Path));
                    }
                }
            }
            catch (InvalidDataException ex) { throw new ProjectFormatException("not a project file", ex); }
            catch (SerializationException ex) { throw new ProjectFormatException("invalid project document", ex); }
            return result;
        }

        private static void Serialize(ProjectDocument document, Stream stream)
        {
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false, true, "  "))
                new DataContractJsonSerializer(typeof(ProjectDocument)).WriteObject(writer, document);
        }

        private static byte[] SerializeToBytes(ProjectDocument document)
        {
            using (var memory = new MemoryStream())
            {
                Serialize(document, memory);
                return memory.ToArray();
            }
        }

        private static ProjectDocument Deserialize(byte[] bytes)
        {
            ProjectDocument document;
            using (var memory = new MemoryStream(bytes))
                document = (ProjectDocument)new DataContractJsonSerializer(typeof(ProjectDocument)).ReadObject(memory);
            return Normalize(document);
        }

        // DataContractの読み込みはコンストラクタを通らないため、欠けた項目をここで既定値に揃える。
        private static ProjectDocument Normalize(ProjectDocument document)
        {
            if (document == null) return null;
            document.AppVersion = document.AppVersion ?? "";
            document.PreviewMode = document.PreviewMode ?? "";
            document.ColorBlendMode = document.ColorBlendMode ?? "";
            document.EffectClip = document.EffectClip ?? new ProjectClip();
            document.PlayerClips = document.PlayerClips ?? new List<ProjectClip>();
            document.Keys = document.Keys ?? new List<ProjectKey>();
            document.Folders = (document.Folders ?? new List<ProjectFolder>()).Where(f => f != null).ToList();
            document.PlayerClips = document.PlayerClips.Where(c => c != null).ToList();
            document.Keys = document.Keys.Where(k => k != null).ToList();
            // メモは壊れた値でも読み込めるよう、座標・幅・文字を整え、数の上限で切る。
            document.Memos = (document.Memos ?? new List<ProjectMemo>()).Where(m => m != null).Take(MemoText.MaxMemos).ToList();
            foreach (ProjectMemo memo in document.Memos)
            {
                memo.X = MemoText.SafeCoordinate(memo.X);
                memo.Y = MemoText.SafeCoordinate(memo.Y);
                memo.Width = MemoText.SafeWidth(memo.Width);
                memo.Text = MemoText.Sanitize(memo.Text);
            }
            foreach (ProjectFolder folder in document.Folders)
            {
                folder.Name = folder.Name ?? "";
                folder.Images = (folder.Images ?? new List<ProjectImage>()).Where(i => i != null).ToList();
                foreach (ProjectImage image in folder.Images) image.Name = image.Name ?? "";
            }
            return document;
        }

        private static byte[] ReadAll(Stream stream, long limit)
        {
            using (var memory = new MemoryStream())
            {
                CopyBounded(stream, memory, limit);
                return memory.ToArray();
            }
        }

        // 宣言された大きさが嘘でも、実際に展開されるバイト数が上限を超えたら止める（zip爆弾対策）。
        private static long CopyBounded(Stream input, Stream output, long limit)
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > limit) throw new ProjectFormatException("entry is larger than allowed", null);
                output.Write(buffer, 0, read);
            }
            return total;
        }

        // Windowsの予約名（CON・NUL・COM1 など）は、拡張子が付いていてもデバイスとして扱われるため使わない。
        private static readonly HashSet<string> ReservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        internal static string SafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "image";
            foreach (char invalid in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            name = name.TrimEnd('.', ' ');
            if (name.Length == 0) return "image";
            if (name.Length > 120)
            {
                string extension = System.IO.Path.GetExtension(name);
                if (extension.Length > 16) extension = "";
                name = name.Substring(0, 120 - extension.Length) + extension;
            }
            int dot = name.IndexOf('.');
            string stem = dot >= 0 ? name.Substring(0, dot) : name;
            if (ReservedNames.Contains(stem.TrimEnd(' '))) name = "_" + name;
            return name;
        }

        private static string UniqueName(string name, HashSet<string> used)
        {
            string candidate = name;
            string stem = System.IO.Path.GetFileNameWithoutExtension(name);
            string extension = System.IO.Path.GetExtension(name);
            for (int counter = 2; !used.Add(candidate); counter++)
                candidate = stem + "_" + counter + extension;
            return candidate;
        }
    }
}
