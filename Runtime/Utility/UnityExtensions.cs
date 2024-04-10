using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class UnityExtensions : MonoBehaviour
{
    public static List<Material> RecursiveMaterialSearch(string path = "", bool relative = true)
    {
        var materials = new List<Material>();
        var searchPath = relative ? Path.Combine(Application.dataPath, path) : path;
        foreach (var directory in Directory.GetDirectories(searchPath))
        {
            materials.AddRange(RecursiveMaterialSearch(directory, false));
        }

        foreach (var filepath in Directory.GetFiles(searchPath))
        {
            if (filepath.EndsWith(".mat"))
            {
                var assetPath = SplitPathAtInclusive(filepath, "Assets");
                materials.Add(AssetDatabase.LoadAssetAtPath<Material>(assetPath));
            }
        }

        return materials;
    }

    public static string SplitPathAtInclusive(string absolutePath, string splitName)
    {
        var latterPath = absolutePath.Split(splitName)[1];
        return splitName + latterPath.Replace("\\", "/");
    }
}
