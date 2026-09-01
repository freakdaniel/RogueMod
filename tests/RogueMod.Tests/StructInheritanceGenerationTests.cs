using RogueMod.Sdk;
using Xunit;

namespace RogueMod.Tests;

public sealed class StructInheritanceGenerationTests
{
    [Fact]
    public void GeneratedAtomicStructIncludesInheritedFields()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"RogueMod-struct-inheritance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);

        try
        {
            var jmapPath = Path.Combine(testDirectory, "inherited-struct.jmap");
            File.WriteAllText(
                jmapPath,
                """
                {
                  "metadata": {
                    "engine_version": { "major": 5, "minor": 6 },
                    "timestamp": "2026-08-28T00:00:00Z"
                  },
                  "objects": {
                    "/Script/CoreUObject.Vector": {
                      "type": "ScriptStruct",
                      "properties_size": 24,
                      "min_alignment": 8,
                      "struct_flags": "STRUCT_IsPlainOldData",
                      "properties": [
                        { "name": "X", "type": "DoubleProperty", "offset": 0, "array_dim": 1, "size": 8, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" },
                        { "name": "Y", "type": "DoubleProperty", "offset": 8, "array_dim": 1, "size": 8, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" },
                        { "name": "Z", "type": "DoubleProperty", "offset": 16, "array_dim": 1, "size": 8, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" }
                      ]
                    },
                    "/Script/Engine.Vector_NetQuantize": {
                      "type": "ScriptStruct",
                      "super_struct": "/Script/CoreUObject.Vector",
                      "properties_size": 24,
                      "min_alignment": 8,
                      "struct_flags": "STRUCT_Atomic | STRUCT_IsPlainOldData",
                      "properties": []
                    },
                    "/Script/Test.AxisMode": {
                      "type": "Enum",
                      "names": [
                        ["AxisMode::Disabled", 0],
                        ["AxisMode::Enabled", 1]
                      ]
                    },
                    "/Script/Test.NonNumericAxes": {
                      "type": "ScriptStruct",
                      "properties_size": 3,
                      "min_alignment": 1,
                      "struct_flags": "STRUCT_IsPlainOldData",
                      "properties": [
                        { "name": "X", "type": "EnumProperty", "enum": "/Script/Test.AxisMode", "offset": 0, "array_dim": 1, "size": 1, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" },
                        { "name": "Y", "type": "EnumProperty", "enum": "/Script/Test.AxisMode", "offset": 1, "array_dim": 1, "size": 1, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" },
                        { "name": "Z", "type": "EnumProperty", "enum": "/Script/Test.AxisMode", "offset": 2, "array_dim": 1, "size": 1, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" }
                      ]
                    }
                  },
                  "vtables": {}
                }
                """);

            var model = new JMapImporter().Import(jmapPath);
            var outputDirectory = Path.Combine(testDirectory, "sdk");
            var result = new CSharpSdkGenerator().Generate(model, outputDirectory, "RogueMod.TestSdk");
            var source = File.ReadAllText(result.SourcePath);

            var wrapperStart = source.IndexOf(
                "public readonly record struct Vector_NetQuantize",
                StringComparison.Ordinal);
            Assert.True(wrapperStart >= 0, "The inherited atomic struct wrapper was not generated.");

            var wrapperEnd = source.IndexOf("\npublic ", wrapperStart + 1, StringComparison.Ordinal);
            var wrapper = wrapperEnd >= 0 ? source[wrapperStart..wrapperEnd] : source[wrapperStart..];
            Assert.Contains("public double X { get; init; }", wrapper, StringComparison.Ordinal);
            Assert.Contains("public double Y { get; init; }", wrapper, StringComparison.Ordinal);
            Assert.Contains("public double Z { get; init; }", wrapper, StringComparison.Ordinal);
            Assert.Contains("new(\"X\", \"DoubleProperty\", 0, 8", wrapper, StringComparison.Ordinal);
            Assert.Contains("new(\"Y\", \"DoubleProperty\", 8, 8", wrapper, StringComparison.Ordinal);
            Assert.Contains("new(\"Z\", \"DoubleProperty\", 16, 8", wrapper, StringComparison.Ordinal);
            Assert.Contains("public override string ToString()", wrapper, StringComparison.Ordinal);

            var nonNumericStart = source.IndexOf(
                "public readonly record struct NonNumericAxes",
                StringComparison.Ordinal);
            Assert.True(nonNumericStart >= 0, "The non-numeric axis struct wrapper was not generated.");
            var nonNumericEnd = source.IndexOf("\npublic ", nonNumericStart + 1, StringComparison.Ordinal);
            var nonNumericWrapper = nonNumericEnd >= 0
                ? source[nonNumericStart..nonNumericEnd]
                : source[nonNumericStart..];
            Assert.DoesNotContain("public override string ToString()", nonNumericWrapper, StringComparison.Ordinal);
            Assert.DoesNotContain("DeadzoneRogueInventory", source, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }
}
