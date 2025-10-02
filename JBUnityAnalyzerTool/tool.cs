using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using YamlDotNet.RepresentationModel;

namespace JBUnityAnalyzerTool
{
    public static class Program
    {
        #region Main

        public static async Task Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.WriteLine("Usage: ./tool.exe <unity_project_path> <output_folder_path>");
                return;
            }

            var unityProjectPath = args[0];
            var outputFolderPath = args[1];

            if (!Directory.Exists(unityProjectPath))
            {
                Console.WriteLine($"Error: Unity project path not found at '{unityProjectPath}'");
                return;
            }

            Directory.CreateDirectory(outputFolderPath);

            await ProcessProject(unityProjectPath, outputFolderPath);
        }

        // main function to process the whole project
        private static async Task ProcessProject(string projectPath, string outputPath)
        {
            // gather all scene and script files (This has been outlined for clarity, if you want it to only parse actual specific unity extensions it needs to have /Assets/ in the path
            // , if not it will parse temporary generated .unity files as well)
            var sceneFiles = Directory.GetFiles(projectPath + "/Assets", "*.unity", SearchOption.AllDirectories);
            var scriptFiles = Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories);

            // create new dictionaries for storing GUIDs and used scripts GUIDs
            var scriptGuids = new ConcurrentDictionary<string, string>();
            var usedScriptGuids = new ConcurrentDictionary<string, bool>();

            // seperate task processing for scripts and scenes
            var scriptTasks = scriptFiles.Select(async scriptFile =>
            {
                Console.WriteLine($"Processing {scriptFile}");
                await ProcessScriptFile(scriptFile, scriptGuids);
            });
            await Task.WhenAll(scriptTasks);

            var sceneTasks = sceneFiles.Select(async sceneFile =>
            {
                Console.WriteLine($"Processing scene: {sceneFile}, output to {outputPath}");
                await ProcessSceneFile(sceneFile, outputPath, scriptGuids, usedScriptGuids);
            });
            await Task.WhenAll(sceneTasks);

            var allGuids = new HashSet<string>(scriptGuids.Keys);
            allGuids.ExceptWith(usedScriptGuids.Keys);

            // write to CSV file, as described in the assignment page.
            var csv = new StringBuilder();
            csv.AppendLine("Relative Path,GUID");
            foreach (var guid in allGuids)
            {
                var absolutePath = scriptGuids[guid];
                var relativePath = Path.GetRelativePath(projectPath, absolutePath).Replace('\\', '/');
                csv.AppendLine($"{relativePath},{guid}");
            }

            // save CSV file
            await File.WriteAllTextAsync(Path.Combine(outputPath, "UnusedScripts.csv"), csv.ToString());

            Console.WriteLine("Analysis complete.");
        }

        #endregion

        #region Processing Methods

        // simple method for retrieving and storing script GUIDs
        private static async Task ProcessScriptFile(string scriptFile, ConcurrentDictionary<string, string> scriptGuids)
        {
            var metaFile = $"{scriptFile}.meta";
            if (File.Exists(metaFile))
            {
                var metaContent = await File.ReadAllTextAsync(metaFile);
                var guidMatch = Regex.Match(metaContent, @"guid: (\w+)");
                if (guidMatch.Success)
                {
                    scriptGuids[guidMatch.Groups[1].Value] = scriptFile;
                }
            }
        }

        // main method for processing scene files
        private static async Task ProcessSceneFile(string sceneFile, string outputPath,
            ConcurrentDictionary<string, string> scriptGuids, ConcurrentDictionary<string, bool> usedScripts)
        {
            // start loading YAML content
            string sceneContent = await File.ReadAllTextAsync(sceneFile);
            var yaml = new YamlStream();
            yaml.Load(new StringReader(sceneContent));

            var gameObjects = new Dictionary<string, GameObject>();
            var transforms = new Dictionary<string, Transform>();
            var components = new List<Component>();

            // start parsing YAML documents
            foreach (var document in yaml.Documents)
            {
                if (!(document.RootNode is YamlMappingNode rootNode) ||
                    string.IsNullOrEmpty(rootNode.Anchor.Value)) continue;

                var fileId = rootNode.Anchor;
                string? objectType = ((YamlScalarNode)rootNode.Children.First().Key).Value;
                var properties = (YamlMappingNode)rootNode.Children.First().Value;

                // categorizing each object type
                switch (objectType)
                {
                    case "GameObject":
                        gameObjects[fileId.Value] = ParseGameObject(properties);
                        break;
                    case "Transform":
                        transforms[fileId.Value] = ParseTransform(properties, fileId.Value);
                        break;
                    case "MonoBehaviour":
                        components.Add(ParseComponent(properties));
                        break;
                    // more cases can be added here if needed
                }
            }

            // check if script has any usage
            var scriptUsageTasks = components.Select(async component =>
            {
                if (!string.IsNullOrEmpty(component.ScriptGuid) && scriptGuids.ContainsKey(component.ScriptGuid))
                {
                    if (await IsScriptUsed(scriptGuids[component.ScriptGuid], component))
                    {
                        usedScripts[component.ScriptGuid] = true;
                    }
                }
            });
            await Task.WhenAll(scriptUsageTasks);

            // start building hierarchy from root transforms
            var rootTransforms = transforms.Values.Where(t => t.FatherId == "0").ToList();
            var hierarchy = new StringBuilder();
            foreach (var root in rootTransforms)
            {
                BuildHierarchy(root, transforms, gameObjects, 0, hierarchy);
            }

            var sceneName = Path.GetFileNameWithoutExtension(sceneFile);
            await File.WriteAllTextAsync(Path.Combine(outputPath, $"{sceneName}.unity.dump"), hierarchy.ToString());
        }

        #endregion


        #region Auxiliary Methods

        private static GameObject ParseGameObject(YamlMappingNode properties)
        {
            return new GameObject { Name = properties.GetScalarValue("m_Name") ?? "Unnamed" };
        }

        private static Transform ParseTransform(YamlMappingNode properties, string ownId)
        {
            return new Transform
            {
                OwnId = ownId,
                GameObjectId = properties.GetFileIdFromMapping("m_GameObject") ?? "0",
                FatherId = properties.GetFileIdFromMapping("m_Father") ?? "0"
            };
        }

        private static Component ParseComponent(YamlMappingNode properties)
        {
            var standardKeys = new HashSet<string>
            {
                "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset",
                "m_GameObject", "m_Enabled", "m_EditorHideFlags", "m_EditorClassIdentifier", "m_Script"
            };

            List<string?> serializedFields = properties.Children
                .Select(c => ((YamlScalarNode)c.Key).Value)
                .Where(k => !standardKeys.Contains(k))
                .ToList();

            return new Component
            {
                ScriptGuid = properties.GetGuidFromMapping("m_Script") ?? "",
                SerializedFields = serializedFields
            };
        }

        // normalize names for comparison (if this was not here, some of the scripts would still be marked as unused)
        private static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var n = name;
            if (n.StartsWith("m_")) n = n.Substring(2);
            n = n.TrimStart('_');
            return n.ToLowerInvariant();
        }

        #endregion

        // recursivly build hierarchy from transforms
        private static void BuildHierarchy(Transform currentTransform, Dictionary<string, Transform> allTransforms,
            Dictionary<string, GameObject> allGameObjects, int level, StringBuilder hierarchy)
        {
            if (!allGameObjects.TryGetValue(currentTransform.GameObjectId, out var go)) return;

            hierarchy.AppendLine($"{new string('-', level)}{go.Name}");

            var children = allTransforms.Values.Where(t => t.FatherId == currentTransform.OwnId);
            foreach (var child in children)
            {
                BuildHierarchy(child, allTransforms, allGameObjects, level + 1, hierarchy);
            }
        }

        // this was the hardest part of the assignment, took me a while to figure out how to properly parse C# code and check for usage
        // this uses Roslyn to parse the C# code, look for Monobehaviour derived classes, and then check if any of the serialized fields are used in the script
        private static async Task<bool> IsScriptUsed(string scriptPath, Component component)
        {
            if (!File.Exists(scriptPath)) return false;

            var scriptContent = await File.ReadAllTextAsync(scriptPath);
            var tree = CSharpSyntaxTree.ParseText(scriptContent);
            var root = await tree.GetRootAsync();

            var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                                .FirstOrDefault(c => c.BaseList != null &&
                                                     c.BaseList.Types.Any(t =>
                                                         t.Type.ToString().Contains("MonoBehaviour")))
                            ?? root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();

            if (classNode == null) return true;

            var scriptMembers = new HashSet<string>(
                classNode.DescendantNodes().OfType<FieldDeclarationSyntax>()
                    .SelectMany(f => f.Declaration.Variables.Select(v => NormalizeName(v.Identifier.Text)))
                    .Concat(classNode.DescendantNodes().OfType<PropertyDeclarationSyntax>()
                        .Select(p => NormalizeName(p.Identifier.Text)))
            );

            if (scriptMembers.Count == 0) return true;

            foreach (var serialized in component.SerializedFields)
                if (scriptMembers.Contains(NormalizeName(serialized)))
                    return true;

            return true;
        }
    }

    #region Yaml Extensions/Specifiers

    // extension methods for easier YAML parsing
    public static class YamlNodeExtensions
    {
        public static string? GetScalarValue(this YamlMappingNode node, string key)
        {
            var scalarKey = new YamlScalarNode(key);
            return node.Children.TryGetValue(scalarKey, out var value) && value is YamlScalarNode scalarValue
                ? scalarValue.Value
                : String.Empty;
        }

        public static string? GetFileIdFromMapping(this YamlMappingNode node, string key)
        {
            var scalarKey = new YamlScalarNode(key);
            return node.Children.TryGetValue(scalarKey, out var value) && value is YamlMappingNode mappingValue
                ? mappingValue.GetScalarValue("fileID")
                : String.Empty;
        }

        public static string? GetGuidFromMapping(this YamlMappingNode node, string key)
        {
            var scalarKey = new YamlScalarNode(key);
            return node.Children.TryGetValue(scalarKey, out var value) && value is YamlMappingNode mappingValue
                ? mappingValue.GetScalarValue("guid")
                : String.Empty;
        }
    }

    public class GameObject
    { 
        public string Name { get; set; }
    }

    public class Transform
    {
        public string OwnId { get; set; }
        public string GameObjectId { get; set; }
        public string FatherId { get; set; }
    }

    public class Component
    {
        public string ScriptGuid { get; set; }
        public List<string> SerializedFields { get; set; } = new();
    }

    #endregion
}