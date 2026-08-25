namespace RogueMod.Core.Mods;

internal static class ModPackageMetadataValidator
{
    public static void ValidateAssets(string packageRoot, ModManifest manifest)
    {
        ValidateAsset(packageRoot, manifest.Icon, "icon");
        foreach (var image in manifest.Images ?? [])
        {
            ValidateAsset(packageRoot, image, "image");
        }
    }

    private static void ValidateAsset(string packageRoot, string? relativePath, string field)
    {
        if (relativePath is null)
        {
            return;
        }
        var path = ModPackageFileSystem.ResolveInside(packageRoot, relativePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Mod {field} does not exist: {path}", path);
        }
        ModPackageFileSystem.RejectLink(path);
    }
}
