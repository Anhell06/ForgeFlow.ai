using System.Text;
using System.Text.Json.Nodes;

namespace ForgeFlow.Nodes;

/// <summary>Writes a minimal binary glTF (one unit cube) for offline testing.</summary>
public static class GlbWriter
{
    public static byte[] Cube()
    {
        float[] p =
        [
            -0.5f, -0.5f, 0.5f, 0.5f, -0.5f, 0.5f, 0.5f, 0.5f, 0.5f, -0.5f, 0.5f, 0.5f,
            -0.5f, -0.5f, -0.5f, 0.5f, -0.5f, -0.5f, 0.5f, 0.5f, -0.5f, -0.5f, 0.5f, -0.5f,
        ];
        ushort[] idx =
        [
            0, 1, 2, 0, 2, 3, 1, 5, 6, 1, 6, 2, 5, 4, 7, 5, 7, 6,
            4, 0, 3, 4, 3, 7, 3, 2, 6, 3, 6, 7, 4, 5, 1, 4, 1, 0,
        ];
        var bin = new MemoryStream();
        var bw = new BinaryWriter(bin);
        foreach (var f in p) bw.Write(f);
        var posLen = (int)bin.Length;
        foreach (var i in idx) bw.Write(i);
        var idxLen = (int)bin.Length - posLen;
        while (bin.Length % 4 != 0) bw.Write((byte)0);

        var gltf = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "ForgeFlow mock" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) }),
            ["nodes"] = new JsonArray(new JsonObject { ["mesh"] = 0, ["name"] = "Cube" }),
            ["materials"] = new JsonArray(new JsonObject
            {
                ["pbrMetallicRoughness"] = new JsonObject { ["baseColorFactor"] = new JsonArray(0.55, 0.45, 0.95, 1.0), ["roughnessFactor"] = 0.6 },
            }),
            ["meshes"] = new JsonArray(new JsonObject
            {
                ["primitives"] = new JsonArray(new JsonObject
                {
                    ["attributes"] = new JsonObject { ["POSITION"] = 0 }, ["indices"] = 1, ["material"] = 0,
                }),
            }),
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = bin.Length }),
            ["bufferViews"] = new JsonArray(
                new JsonObject { ["buffer"] = 0, ["byteOffset"] = 0, ["byteLength"] = posLen, ["target"] = 34962 },
                new JsonObject { ["buffer"] = 0, ["byteOffset"] = posLen, ["byteLength"] = idxLen, ["target"] = 34963 }),
            ["accessors"] = new JsonArray(
                new JsonObject
                {
                    ["bufferView"] = 0, ["componentType"] = 5126, ["count"] = 8, ["type"] = "VEC3",
                    ["min"] = new JsonArray(-0.5, -0.5, -0.5), ["max"] = new JsonArray(0.5, 0.5, 0.5),
                },
                new JsonObject { ["bufferView"] = 1, ["componentType"] = 5123, ["count"] = idx.Length, ["type"] = "SCALAR" }),
        };
        var jsonBytes = Encoding.UTF8.GetBytes(gltf.ToJsonString());
        var jsonPadded = jsonBytes.Concat(Enumerable.Repeat((byte)' ', (4 - jsonBytes.Length % 4) % 4)).ToArray();
        var binBytes = bin.ToArray();

        var outStream = new MemoryStream();
        var w = new BinaryWriter(outStream);
        w.Write(0x46546C67u); // "glTF"
        w.Write(2u);
        w.Write((uint)(12 + 8 + jsonPadded.Length + 8 + binBytes.Length));
        w.Write((uint)jsonPadded.Length);
        w.Write(0x4E4F534Au); // "JSON"
        w.Write(jsonPadded);
        w.Write((uint)binBytes.Length);
        w.Write(0x004E4942u); // "BIN\0"
        w.Write(binBytes);
        return outStream.ToArray();
    }
}
