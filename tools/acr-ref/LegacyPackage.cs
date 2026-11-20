using System.Buffers.Binary;
using System.Text;

namespace AcrRef;

/// A cooked legacy package (.uasset + .uexp) as written by `retoc to-legacy` for UE 5.6 (unversioned,
/// FilterEditorOnly). Supports appending names and imports and replacing export data; everything else in
/// the header is copied verbatim and only its offsets are shifted.
///
/// Header order (retoc legacy_asset.rs): summary, name map, import map (32 B each), export map (96 B each),
/// then cell maps / depends / asset registry / preload dependencies / data resources up to TotalHeaderSize.
internal sealed class LegacyPackage
{
    const int ImportSize = 32, ExportSize = 96;

    readonly byte[] _header;
    readonly int _namesOffset, _namesEnd, _importsOffset, _exportsOffset, _exportsEnd, _totalHeaderSize;
    readonly int _origNameCount, _origImportCount, _origExportCount;
    /// Preload dependencies replaced or added with SetExportDeps / AddExport: export -> {SBS, CBS, SBC, CBC} lists. When any
    /// exist, Save rebuilds the whole preload dependency array (the exports' first-dependency indices follow).
    readonly Dictionary<int, List<int>[]> _depOverride = new();
    readonly Dictionary<string, int> _fieldPos = new();
    readonly List<int> _shiftablePos = new(), _generationsPos = new();
    readonly List<string> _names = new();
    readonly List<int> _nameHashes = new();
    readonly List<byte[]> _imports = new();
    readonly List<byte[]> _exports = new();
    readonly List<byte[]> _exportData = new();
    readonly byte[] _uexpTag;

    public string PackageName { get; }
    public IReadOnlyList<string> Names => _names;
    public int ExportCount => _exports.Count;
    public int ImportCount => _imports.Count;

    public LegacyPackage(string uassetPath)
    {
        _header = File.ReadAllBytes(uassetPath);
        var uexp = File.ReadAllBytes(Path.ChangeExtension(uassetPath, ".uexp"));
        var d = _header;
        int p = 0;
        int I32() { int v = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); p += 4; return v; }
        string FStr()
        {
            int len = I32();
            if (len == 0) return "";
            string s = len > 0 ? Encoding.Latin1.GetString(d, p, len - 1) : Encoding.Unicode.GetString(d, p, -len * 2 - 2);
            p += len > 0 ? len : -len * 2;
            return s;
        }
        void Field(string name) { _fieldPos[name] = p; p += 4; }

        if (BinaryPrimitives.ReadUInt32LittleEndian(d) != 0x9E2A83C1) throw new InvalidDataException("not a package");
        p = 4;
        int legacy = I32();
        if (legacy != -9) throw new NotSupportedException($"legacy file version {legacy}, expected -9 (UE 5.6)");
        p += 4 * 4;                                   // UE3, UE4, UE5, licensee versions
        p += 20;                                      // saved hash
        Field("TotalHeaderSize");
        int customVersions = I32(); p += customVersions * 20;
        _packageNamePos = p;
        PackageName = FStr();
        _packageNameEnd = p;
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p)); p += 4;
        if ((flags & 0x80000000) == 0) throw new NotSupportedException("package is not FilterEditorOnly");
        Field("NameCount"); Field("NameOffset");
        Field("SoftObjectPathsCount"); Field("SoftObjectPathsOffset");
        p += 8;                                       // gatherable text data count/offset
        Field("ExportCount"); Field("ExportOffset");
        Field("ImportCount"); Field("ImportOffset");
        Field("CellExportCount"); Field("CellExportOffset");
        Field("CellImportCount"); Field("CellImportOffset");
        Field("MetaDataOffset"); Field("DependsOffset");
        Field("SoftPackageReferencesCount"); Field("SoftPackageReferencesOffset");
        Field("SearchableNamesOffset"); Field("ThumbnailTableOffset");
        int generations = I32();
        for (int g = 0; g < generations; g++) { _generationsPos.Add(p); p += 8; }
        for (int e = 0; e < 2; e++) { p += 2 + 2 + 2 + 4; FStr(); }   // saved-by / compatible-with engine versions
        p += 4 + 4 + 4;                               // compression flags, compressed chunk count, package source
        int additional = I32(); for (int a = 0; a < additional; a++) FStr();
        Field("AssetRegistryDataOffset");
        _fieldPos["BulkDataStartOffset"] = p; p += 8;
        Field("WorldTileInfoDataOffset");
        int chunkIds = I32(); p += chunkIds * 4;
        Field("PreloadDependencyCount"); Field("PreloadDependencyOffset");
        Field("NamesReferencedFromExportDataCount");
        _fieldPos["PayloadTocOffset"] = p; p += 8;
        Field("DataResourceOffset");

        int Get(string f) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(_fieldPos[f]));
        _namesOffset = Get("NameOffset");
        if (p != _namesOffset) throw new InvalidDataException($"summary parse ended at {p}, name map starts at {_namesOffset}");
        _totalHeaderSize = Get("TotalHeaderSize");

        _origNameCount = Get("NameCount");
        for (int n = 0; n < _origNameCount; n++) { _names.Add(FStr()); _nameHashes.Add(I32()); }
        _namesEnd = p;
        _importsOffset = Get("ImportOffset");
        _origImportCount = Get("ImportCount");
        if (_importsOffset != _namesEnd || Get("SoftObjectPathsOffset") != _namesEnd)
            throw new InvalidDataException("import map does not directly follow the name map");
        for (int i = 0; i < _origImportCount; i++) _imports.Add(d.AsSpan(_importsOffset + i * ImportSize, ImportSize).ToArray());
        _exportsOffset = Get("ExportOffset");
        if (_exportsOffset != _importsOffset + _origImportCount * ImportSize) throw new InvalidDataException("export map does not follow the import map");
        int exportCount = Get("ExportCount");
        for (int e = 0; e < exportCount; e++) _exports.Add(d.AsSpan(_exportsOffset + e * ExportSize, ExportSize).ToArray());
        _exportsEnd = _exportsOffset + exportCount * ExportSize;
        _origExportCount = exportCount;

        foreach (var f in new[] { "CellExportOffset", "CellImportOffset", "MetaDataOffset", "DependsOffset", "SoftPackageReferencesOffset",
                                  "SearchableNamesOffset", "ThumbnailTableOffset", "AssetRegistryDataOffset",
                                  "WorldTileInfoDataOffset", "PreloadDependencyOffset", "DataResourceOffset" })
            _shiftablePos.Add(_fieldPos[f]);

        long dataTotal = 0;
        for (int e = 0; e < exportCount; e++)
        {
            long off = SerialOffset(e) - _totalHeaderSize, size = SerialSize(e);
            _exportData.Add(uexp.AsSpan((int)off, (int)size).ToArray());
            dataTotal += size;
        }
        if (dataTotal + 4 != uexp.Length) throw new InvalidDataException($"exports cover {dataTotal} of {uexp.Length - 4} uexp bytes");
        _uexpTag = uexp.AsSpan(uexp.Length - 4).ToArray();
        for (int e = 1; e < exportCount; e++)
            if (SerialOffset(e) < SerialOffset(e - 1)) throw new NotSupportedException("exports are not stored in export-map order");
    }

    long SerialSize(int e) => BinaryPrimitives.ReadInt64LittleEndian(_exports[e].AsSpan(28));
    long SerialOffset(int e) => BinaryPrimitives.ReadInt64LittleEndian(_exports[e].AsSpan(36));

    public string NameAt(int index, int number) => number == 0 ? _names[index] : $"{_names[index]}_{number - 1}";
    string FNameAt(byte[] entry, int at) => NameAt(BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(at)), BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(at + 4)));

    public string ExportName(int e) => FNameAt(_exports[e], 16);
    public int ExportOuter(int e) => BinaryPrimitives.ReadInt32LittleEndian(_exports[e].AsSpan(12));
    public string ExportClass(int e)
    {
        int c = BinaryPrimitives.ReadInt32LittleEndian(_exports[e].AsSpan(0));
        return c < 0 ? ImportName(-c - 1) : c > 0 ? ExportName(c - 1) : "";
    }
    public void SetExportOuter(int e, int outer) => BinaryPrimitives.WriteInt32LittleEndian(_exports[e].AsSpan(12), outer);
    public uint ExportFlags(int e) => BinaryPrimitives.ReadUInt32LittleEndian(_exports[e].AsSpan(24));
    public void SetExportFlags(int e, uint flags) => BinaryPrimitives.WriteUInt32LittleEndian(_exports[e].AsSpan(24), flags);

    /// Preload dependencies of an export: (kind, header position, package index). Kinds in the order of the export map's
    /// counts: SerializationBeforeSerialization, CreateBeforeSerialization, SerializationBeforeCreate, CreateBeforeCreate.
    public List<(string kind, int pos, int index)> ExportDeps(int e)
    {
        if (_depOverride.TryGetValue(e, out var ov))
        {
            var o = new List<(string, int, int)>();
            string[] ks = ["SBS", "CBS", "SBC", "CBC"];
            for (int k = 0; k < 4; k++) foreach (var i in ov[k]) o.Add((ks[k], -1, i));
            return o;
        }
        int first = BinaryPrimitives.ReadInt32LittleEndian(_exports[e].AsSpan(76));
        var res = new List<(string, int, int)>();
        if (first < 0) return res;
        int at = BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(_fieldPos["PreloadDependencyOffset"])) + first * 4;
        string[] kinds = ["SBS", "CBS", "SBC", "CBC"];
        for (int k = 0; k < 4; k++)
            for (int n = BinaryPrimitives.ReadInt32LittleEndian(_exports[e].AsSpan(80 + k * 4)); n > 0; n--, at += 4)
                res.Add((kinds[k], at, BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(at))));
        return res;
    }

    /// Rewrites one preload dependency in place (positions come from ExportDeps; the region is copied verbatim on save).
    public void SetDep(int pos, int index)
    {
        if (pos < 0) throw new InvalidOperationException("this export's dependencies were replaced (SetExportDeps / AddExport); use SetExportDeps");
        if (pos < _exportsEnd || pos + 4 > _totalHeaderSize) throw new ArgumentOutOfRangeException(nameof(pos));
        BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(pos), index);
    }

    public string ImportName(int i) => FNameAt(_imports[i], 20);
    public string ImportClassName(int i) => FNameAt(_imports[i], 8);
    public string ImportClassPackage(int i) => FNameAt(_imports[i], 0);
    public int ImportOuter(int i) => BinaryPrimitives.ReadInt32LittleEndian(_imports[i].AsSpan(16));
    public byte[] ImportEntry(int i) => _imports[i];
    public byte[] ExportEntry(int e) => (byte[])_exports[e].Clone();

    /// FPackageIndex (negative) of the import with this class package / class / name and outer, 0 if there is none.
    public int FindImport(string classPackage, string className, string name, int outerIndex)
    {
        for (int i = 0; i < _imports.Count; i++)
            if (ImportName(i) == name && ImportClassName(i) == className && ImportClassPackage(i) == classPackage && ImportOuter(i) == outerIndex) return -(i + 1);
        return 0;
    }

    /// Replaces an export's preload dependencies (FPackageIndex values per kind).
    public void SetExportDeps(int e, IEnumerable<int> sbs, IEnumerable<int> cbs, IEnumerable<int> sbc, IEnumerable<int> cbc) =>
        _depOverride[e] = [sbs.ToList(), cbs.ToList(), sbc.ToList(), cbc.ToList()];

    /// Appends an export (the last in the export map) and returns its 0-based index; its FPackageIndex is that + 1. The entry is
    /// copied from entryTemplate (96 bytes, e.g. another export of the same class) with class, super, template, outer, name and
    /// flags patched; serial size / offset follow from data on Save. Dependencies are given per kind. The depends table grows
    /// by one empty entry.
    public int AddExport(byte[] entryTemplate, string name, int classIndex, int superIndex, int templateIndex, int outerIndex, uint flags, byte[] data,
                         IEnumerable<int> sbs, IEnumerable<int> cbs, IEnumerable<int> sbc, IEnumerable<int> cbc)
    {
        if (entryTemplate.Length != ExportSize) throw new ArgumentException("export entry must be 96 bytes");
        int nameIndex = AddExportName(name);
        var e = (byte[])entryTemplate.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(0), classIndex);
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(4), superIndex);
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(8), templateIndex);
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(12), outerIndex);
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(16), nameIndex);
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(20), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(24), flags);
        _exports.Add(e);
        _exportData.Add(data);
        int idx = _exports.Count - 1;
        SetExportDeps(idx, sbs, cbs, sbc, cbc);
        return idx;
    }

    /// Drops imports (0-based indices) and renumbers the rest: imports' outers, the exports' class / super / template / outer,
    /// and the preload dependencies are remapped (throws if a kept one still points at a removed import). References inside
    /// export data are the caller's. Returns old 0-based index -> new 0-based index (-1: removed).
    public int[] RemoveImports(IEnumerable<int> remove)
    {
        var rm = remove.ToHashSet();
        var map = new int[_imports.Count];
        int n = 0;
        for (int i = 0; i < map.Length; i++) map[i] = rm.Contains(i) ? -1 : n++;
        int Remap(int pi, string what)
        {
            if (pi >= 0) return pi;
            int m = map[-pi - 1];
            if (m < 0) throw new InvalidOperationException($"{what} still references removed import {pi} ({ImportName(-pi - 1)})");
            return -(m + 1);
        }
        for (int i = 0; i < _imports.Count; i++)
            if (map[i] >= 0) BinaryPrimitives.WriteInt32LittleEndian(_imports[i].AsSpan(16), Remap(ImportOuter(i), $"import {ImportName(i)}"));
        for (int e = 0; e < _exports.Count; e++)
            foreach (var at in new[] { 0, 4, 8, 12 })
                BinaryPrimitives.WriteInt32LittleEndian(_exports[e].AsSpan(at), Remap(BinaryPrimitives.ReadInt32LittleEndian(_exports[e].AsSpan(at)), $"export {ExportName(e)}"));
        for (int e = 0; e < _origExportCount; e++)
            if (!_depOverride.ContainsKey(e))
                foreach (var d in ExportDeps(e))
                    BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(d.pos), Remap(d.index, $"export {ExportName(e)} dependency"));
        foreach (var lists in _depOverride.Values)
            foreach (var l in lists)
                for (int i = 0; i < l.Count; i++) l[i] = Remap(l[i], "dependency");
        var kept = _imports.Where((_, i) => map[i] >= 0).ToList();
        _imports.Clear();
        _imports.AddRange(kept);
        return map;
    }

    public int FindName(string s) => _names.IndexOf(s);
    public int FindExport(string name, string? cls = null, Func<int, bool>? pred = null)
    {
        var hits = Enumerable.Range(0, _exports.Count)
            .Where(e => ExportName(e) == name && (cls is null || ExportClass(e) == cls) && (pred is null || pred(e))).ToList();
        if (hits.Count != 1) throw new InvalidOperationException($"export {name} ({cls}) matched {hits.Count} times");
        return hits[0];
    }

    /// Offset of the header region after the export map (cell maps, depends, preload dependencies, data resources).
    public int HeaderTailStart => _exportsEnd;
    public int HeaderField(string name) => BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(_fieldPos[name]));
    public IEnumerable<string> HeaderFieldNames => _fieldPos.Keys;

    public byte[] GetExportData(int e) => _exportData[e];
    public void SetExportData(int e, byte[] data) => _exportData[e] = data;

    public int AddName(string s)
    {
        int i = _names.IndexOf(s);
        if (i >= 0) return i;
        _names.Add(s);
        _nameHashes.Add(0);
        return _names.Count - 1;
    }

    int? _exportNameCount;

    /// A name referenced from export data. IoStore keeps only the first NamesReferencedFromExportDataCount names for
    /// export data (the rest are header-only), so new ones are inserted at the end of that range and the header-only
    /// names after it move up by one (their references in the import and export maps are remapped); names added with
    /// AddName after the range would be dropped by `retoc to-zen`.
    public int AddExportName(string s)
    {
        _exportNameCount ??= BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(_fieldPos["NamesReferencedFromExportDataCount"]));
        int n = _exportNameCount.Value;
        int i = _names.IndexOf(s);
        if (i >= 0 && i < n) return i;
        if (i >= 0) throw new NotSupportedException($"{s} is a header-only name; moving it into the export-data range is not supported");
        if (BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(_fieldPos["SoftObjectPathsCount"])) != 0)
            throw new NotSupportedException("packages with soft object paths in the header are not supported");
        _names.Insert(n, s);
        _nameHashes.Insert(n, 0);
        void Shift(byte[] entry, int at)
        {
            int v = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(at));
            if (v >= n) BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(at), v + 1);
        }
        foreach (var imp in _imports) { Shift(imp, 0); Shift(imp, 8); Shift(imp, 20); }
        foreach (var exp in _exports) Shift(exp, 16);
        _exportNameCount = n + 1;
        return n;
    }

    /// Appends an import and returns its FPackageIndex (negative).
    public int AddImport(string classPackage, string className, int outerIndex, string objectName)
    {
        var e = new byte[ImportSize];
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(0), AddName(classPackage));
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(8), AddName(className));
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(16), outerIndex);
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(20), AddName(objectName));
        _imports.Add(e);
        return -_imports.Count;
    }

    public (byte[] uasset, byte[] uexp) Save()
    {
        using var nameBytes = new MemoryStream();
        for (int n = 0; n < _names.Count; n++)
        {
            var s = _names[n];
            bool ascii = s.All(c => c < 128);
            var bw = new BinaryWriter(nameBytes);
            if (ascii) { bw.Write(s.Length + 1); bw.Write(Encoding.ASCII.GetBytes(s)); bw.Write((byte)0); }
            else { bw.Write(-(s.Length + 1)); bw.Write(Encoding.Unicode.GetBytes(s)); bw.Write((short)0); }
            bw.Write(_nameHashes[n]); // new names: 0 (unused by the game, retoc writes 0)
        }
        int delta1 = (int)nameBytes.Length - (_namesEnd - _namesOffset);
        int delta2 = (_imports.Count - _origImportCount) * ImportSize;
        int front = delta1 + delta2;                                   // everything from the export map on moves by this
        int newExports = _exports.Count - _origExportCount;
        int exportsDelta = newExports * ExportSize;

        // header tail (cell maps, depends, asset registry, preload dependencies, data resources): new exports add an empty depends
        // entry each, and a changed dependency set rebuilds the preload dependency array
        int Old(string f) => BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(_fieldPos[f]));
        int dependsOld = Old("DependsOffset"), regOld = Old("AssetRegistryDataOffset"), depsOld = Old("PreloadDependencyOffset"), depsCountOld = Old("PreloadDependencyCount");
        int depsEndOld = depsOld + depsCountOld * 4;
        bool rebuildDeps = _depOverride.Count > 0;
        int dependsGrowth = 0, depsDelta = 0;
        var newDeps = new List<int>();
        var firstDep = new int[_exports.Count]; var depCounts = new int[_exports.Count][];
        if (newExports > 0)
        {
            if (regOld - dependsOld != 4 * _origExportCount || regOld > depsOld)
                throw new NotSupportedException($"depends table is {regOld - dependsOld} bytes for {_origExportCount} exports; cannot add exports");
            dependsGrowth = newExports * 4;
        }
        if (rebuildDeps)
        {
            for (int e = 0; e < _exports.Count; e++)
            {
                List<int>[] lists;
                if (_depOverride.TryGetValue(e, out var ov)) lists = ov;
                else
                {
                    var cur = ExportDeps(e);
                    lists = new[] { "SBS", "CBS", "SBC", "CBC" }.Select(k => cur.Where(d => d.kind == k).Select(d => d.index).ToList()).ToArray();
                }
                depCounts[e] = lists.Select(l => l.Count).ToArray();
                firstDep[e] = depCounts[e].Sum() == 0 ? -1 : newDeps.Count;
                foreach (var l in lists) newDeps.AddRange(l);
            }
            if (depsCountOld > 0 || depsOld >= regOld) depsDelta = (newDeps.Count - depsCountOld) * 4;
        }
        int tailLen = _totalHeaderSize - _exportsEnd;
        int dependsEndRel = regOld - _exportsEnd, depsStartRel = depsOld - _exportsEnd, depsEndRel = depsEndOld - _exportsEnd;
        using var tail = new MemoryStream();
        if (!rebuildDeps && newExports == 0) tail.Write(_header, _exportsEnd, tailLen);
        else
        {
            if (depsStartRel < dependsEndRel || depsEndRel > tailLen) throw new NotSupportedException("unexpected header tail layout");
            tail.Write(_header, _exportsEnd, dependsEndRel);
            tail.Write(new byte[dependsGrowth]);
            tail.Write(_header, _exportsEnd + dependsEndRel, depsStartRel - dependsEndRel);
            if (rebuildDeps) foreach (var d in newDeps) tail.Write(BitConverter.GetBytes(d));
            else tail.Write(_header, depsOld, depsCountOld * 4);
            tail.Write(_header, _exportsEnd + depsEndRel, tailLen - depsEndRel);
        }
        int newTotal = _totalHeaderSize + front + exportsDelta + dependsGrowth + depsDelta;

        var h = new byte[newTotal];
        _header.AsSpan(0, _namesOffset).CopyTo(h);
        nameBytes.ToArray().CopyTo(h, _namesOffset);
        int p = _namesEnd + delta1;
        foreach (var imp in _imports) { imp.CopyTo(h, p); p += ImportSize; }
        int exportsAt = p;
        long offset = newTotal, end = newTotal;
        for (int e = 0; e < _exports.Count; e++)
        {
            var entry = (byte[])_exports[e].Clone();
            BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(28), _exportData[e].Length);
            BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(36), offset);
            if (rebuildDeps)
            {
                BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(76), firstDep[e]);
                for (int k = 0; k < 4; k++) BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(80 + k * 4), depCounts[e][k]);
            }
            offset += _exportData[e].Length;
            end = offset;
            entry.CopyTo(h, p); p += ExportSize;
        }
        tail.ToArray().CopyTo(h, p);

        void Set32(string f, int v) => BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(_fieldPos[f]), v);
        Set32("TotalHeaderSize", newTotal);
        Set32("NameCount", _names.Count);
        if (_exportNameCount is { } enc) Set32("NamesReferencedFromExportDataCount", enc);
        Set32("SoftObjectPathsOffset", _namesEnd + delta1);
        Set32("ImportCount", _imports.Count);
        Set32("ImportOffset", _namesEnd + delta1);
        Set32("ExportCount", _exports.Count);
        Set32("ExportOffset", exportsAt);
        foreach (var pos in _shiftablePos)
        {
            int v = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(pos));
            if (v <= 0 || v < _exportsOffset) continue;
            int nv = v + front;
            if (v >= _exportsEnd) nv += exportsDelta;
            if (v >= regOld && newExports > 0) nv += dependsGrowth;
            if (v >= depsEndOld && rebuildDeps) nv += depsDelta;
            BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(pos), nv);
        }
        if (rebuildDeps) Set32("PreloadDependencyCount", newDeps.Count);
        foreach (var g in _generationsPos) BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(g + 4), _names.Count);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(_fieldPos["BulkDataStartOffset"]), end);
        if (_dataResources is not null) h = SpliceDataResources(h);
        if (_newPackageName is not null) h = SplicePackageName(h);

        using var u = new MemoryStream();
        foreach (var data in _exportData) u.Write(data);
        u.Write(_uexpTag);
        return (h, u.ToArray());
    }

    string? _newPackageName;
    byte[]? _dataResources;
    int _packageNamePos, _packageNameEnd;

    /// Renames a name-map entry in place (all references keep their index).
    public void RenameName(int index, string s) { _names[index] = s; _nameHashes[index] = 0; }

    /// Changes the package name in the summary (the name-map entry is renamed separately with RenameName).
    public void SetPackageName(string name) => _newPackageName = name;

    /// The data resource table (bulk data metadata, UE 5.2+): int32 version, int32 count, 44-byte entries
    /// (flags u32, serial offset i64, duplicate serial offset i64, serial size i64, raw size i64, outer index i32,
    /// legacy bulk data flags u32). Inline payloads have serial offsets relative to the export data.
    public const int DataResourceSize = 44;
    public byte[] DataResources()
    {
        int at = HeaderField("DataResourceOffset");
        if (at <= 0) return [];
        int n = BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(at + 4));
        return _header.AsSpan(at, 8 + n * DataResourceSize).ToArray();
    }
    public void SetDataResources(byte[] table) => _dataResources = table;

    byte[] SpliceDataResources(byte[] h)
    {
        int oldAt = HeaderField("DataResourceOffset");
        if (oldAt <= 0) throw new NotSupportedException("package has no data resource table");
        if (BinaryPrimitives.ReadInt64LittleEndian(_header.AsSpan(_fieldPos["PayloadTocOffset"])) > 0)
            throw new NotSupportedException("package has a payload TOC; resizing the data resources is not supported");
        foreach (var pos in _shiftablePos)
            if (BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(pos)) > oldAt)
                throw new NotSupportedException("a header table follows the data resources; resizing them is not supported");
        int at = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(_fieldPos["DataResourceOffset"]));
        int oldLen = DataResources().Length;
        var r = h.AsSpan(0, at).ToArray().Concat(_dataResources!).Concat(h.AsSpan(at + oldLen).ToArray()).ToArray();
        int d = r.Length - h.Length;
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(_fieldPos["TotalHeaderSize"]), BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(_fieldPos["TotalHeaderSize"])) + d);
        ShiftExportData(r, d, BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(_fieldPos["ExportOffset"])), 0);
        return r;
    }

    byte[] SplicePackageName(byte[] h)
    {
        var s = _newPackageName!;
        var fstr = new byte[4 + s.Length + 1];
        BinaryPrimitives.WriteInt32LittleEndian(fstr, s.Length + 1);
        Encoding.ASCII.GetBytes(s).CopyTo(fstr, 4);
        var r = h.AsSpan(0, _packageNamePos).ToArray().Concat(fstr).Concat(h.AsSpan(_packageNameEnd).ToArray()).ToArray();
        int d = fstr.Length - (_packageNameEnd - _packageNamePos);
        int P(string f) => _fieldPos[f] >= _packageNameEnd ? _fieldPos[f] + d : _fieldPos[f];
        void Add(string f) { int v = BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(P(f))); if (v > 0) BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(P(f)), v + d); }
        Add("TotalHeaderSize");
        foreach (var f in new[] { "NameOffset", "SoftObjectPathsOffset", "ExportOffset", "ImportOffset" }) Add(f);
        foreach (var f in _fieldPos.Where(kv => _shiftablePos.Contains(kv.Value)).Select(kv => kv.Key)) Add(f);
        var toc = r.AsSpan(P("PayloadTocOffset"));
        if (BinaryPrimitives.ReadInt64LittleEndian(toc) > 0) BinaryPrimitives.WriteInt64LittleEndian(toc, BinaryPrimitives.ReadInt64LittleEndian(toc) + d);
        ShiftExportData(r, d, BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(P("ExportOffset"))), d);
        return r;
    }

    /// Moves the export data (serial offsets and BulkDataStartOffset) by d bytes; fieldShift: how far the summary
    /// fields after the package name moved.
    void ShiftExportData(byte[] r, int d, int exportsAt, int fieldShift)
    {
        for (int e = 0; e < _exports.Count; e++)
        {
            var s = r.AsSpan(exportsAt + e * ExportSize + 36);
            BinaryPrimitives.WriteInt64LittleEndian(s, BinaryPrimitives.ReadInt64LittleEndian(s) + d);
        }
        var b = r.AsSpan(_fieldPos["BulkDataStartOffset"] + fieldShift);
        BinaryPrimitives.WriteInt64LittleEndian(b, BinaryPrimitives.ReadInt64LittleEndian(b) + d);
    }

    public void SaveTo(string uassetPath)
    {
        var (h, u) = Save();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(uassetPath))!);
        File.WriteAllBytes(uassetPath, h);
        File.WriteAllBytes(Path.ChangeExtension(uassetPath, ".uexp"), u);
    }
}
