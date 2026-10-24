using System;
using System.IO;
using UnityEngine;

namespace Guardian.UnitySim
{
    [Serializable]
    public sealed class GuardianSettingsDocument
    {
        public int SchemaVersion;
        public string Profile;
        public GuardianSimulationSettings Settings;

        public void Validate()
        {
            if (SchemaVersion != 1)
                throw new ArgumentException("Unsupported Guardian settings schema version");
            if (string.IsNullOrWhiteSpace(Profile))
                throw new ArgumentException("Guardian settings profile is required");
            if (Settings == null)
                throw new ArgumentException("Guardian settings payload is required");
            Settings.Validate();
        }
    }

    /// <summary>
    /// Loads an explicitly selected external settings document without placing
    /// operational values in the Unity project or player build.
    /// </summary>
    public static class GuardianSettingsLoader
    {
        public const string EnvironmentVariable = "GUARDIAN_SETTINGS_PATH";
        public const string PathArgument = "-guardianSettingsPath";
        public const string PublicArgument = "-guardianPublicSettings";
        public const string PublicRelativePath = "Assets/StreamingAssets/Guardian/guardian-settings.public.json";
        public const string PrivateRelativePath = ".guardian/guardian-settings.private.json";

        private static readonly string[] RequiredSettingFields =
        {
            "Seed", "MinimumTaskInterval", "MaximumTaskInterval", "MaximumDepartureDelay",
            "MaximumOpenTasks", "RandomFaults", "MinimumFaultInterval", "MaximumFaultInterval",
            "WarningThreshold", "LimitedThreshold", "MealSeconds", "CollectEmptyOnDelivery",
            "MotorAcceleration", "MaximumSpeed"
        };

        private static readonly string[] PedestrianSettingFields =
        {
            "PedestrianDemoEnabled", "PedestrianSpeed",
            "PedestrianFirstCrossing", "PedestrianCrossingInterval", "PedestrianSafetyMargin",
            "PedestrianClearSeconds"
        };

        public static GuardianSimulationSettings LoadOverrideOrKeep(
            GuardianSimulationSettings current,
            string[] arguments = null,
            string projectRoot = null)
        {
            if (current == null) current = new GuardianSimulationSettings();
            current.Validate();

            arguments = arguments ?? Environment.GetCommandLineArgs();
            projectRoot = string.IsNullOrWhiteSpace(projectRoot)
                ? ResolveProjectRoot()
                : Path.GetFullPath(projectRoot);

            string selectedPath = FindArgumentValue(arguments, PathArgument);
            bool explicitSelection = !string.IsNullOrWhiteSpace(selectedPath);

            if (!explicitSelection)
            {
                selectedPath = Environment.GetEnvironmentVariable(EnvironmentVariable);
                explicitSelection = !string.IsNullOrWhiteSpace(selectedPath);
            }

            if (!explicitSelection && HasArgument(arguments, PublicArgument))
            {
                selectedPath = ResolvePublicPath(projectRoot);
                explicitSelection = true;
            }

            if (!explicitSelection)
            {
                string localPrivatePath = Path.Combine(projectRoot, PrivateRelativePath);
                if (File.Exists(localPrivatePath)) selectedPath = localPrivatePath;
            }

            if (string.IsNullOrWhiteSpace(selectedPath)) return current;
            if (!Path.IsPathRooted(selectedPath)) selectedPath = Path.Combine(projectRoot, selectedPath);

            GuardianSettingsDocument document = LoadDocument(selectedPath);
            Debug.Log("[Guardian] Loaded validated settings from an external document");
            return document.Settings;
        }

        public static GuardianSettingsDocument LoadDocument(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Guardian settings path is required", nameof(path));

            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("Guardian settings file was explicitly selected but not found", fullPath);

            string json;
            try
            {
                json = File.ReadAllText(fullPath);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException("Guardian settings JSON could not be read", exception);
            }

            RequireField(json, "SchemaVersion");
            RequireField(json, "Profile");
            RequireField(json, "Settings");
            foreach (string field in RequiredSettingFields) RequireField(json, field);
            int pedestrianFields=0;
            foreach (string field in PedestrianSettingFields) if (HasField(json,field)) pedestrianFields++;
            if (pedestrianFields>0 && pedestrianFields<PedestrianSettingFields.Length)
                foreach (string field in PedestrianSettingFields) RequireField(json,field);

            GuardianSettingsDocument document;
            try { document = JsonUtility.FromJson<GuardianSettingsDocument>(json); }
            catch (Exception exception) { throw new InvalidDataException("Guardian settings JSON is malformed", exception); }

            if (document == null)
                throw new InvalidDataException("Guardian settings JSON is empty or malformed");
            if (document.Settings!=null && pedestrianFields==0) ApplyPedestrianDefaults(document.Settings);
            document.Validate();
            return document;
        }

        private static void ApplyPedestrianDefaults(GuardianSimulationSettings settings)
        {
            GuardianSimulationSettings defaults=new GuardianSimulationSettings();
            settings.PedestrianDemoEnabled=defaults.PedestrianDemoEnabled;
            settings.PedestrianSpeed=defaults.PedestrianSpeed;
            settings.PedestrianFirstCrossing=defaults.PedestrianFirstCrossing;
            settings.PedestrianCrossingInterval=defaults.PedestrianCrossingInterval;
            settings.PedestrianSafetyMargin=defaults.PedestrianSafetyMargin;
            settings.PedestrianClearSeconds=defaults.PedestrianClearSeconds;
        }

        private static string ResolvePublicPath(string projectRoot)
        {
            string projectPath = Path.Combine(projectRoot, PublicRelativePath);
            if (File.Exists(projectPath)) return projectPath;
            return Path.Combine(Application.streamingAssetsPath, "Guardian/guardian-settings.public.json");
        }

        private static void RequireField(string json, string field)
        {
            if (!HasField(json,field))
                throw new InvalidDataException("Guardian settings field is required: " + field);
        }

        private static bool HasField(string json,string field)=>
            json.IndexOf("\""+field+"\"",StringComparison.Ordinal)>=0;

        private static string ResolveProjectRoot()
        {
            string dataPath = Application.dataPath;
            DirectoryInfo assets = new DirectoryInfo(dataPath);
            return assets.Parent == null ? Directory.GetCurrentDirectory() : assets.Parent.FullName;
        }

        private static bool HasArgument(string[] arguments, string name)
        {
            foreach (string argument in arguments)
                if (string.Equals(argument, name, StringComparison.Ordinal)) return true;
            return false;
        }

        private static string FindArgumentValue(string[] arguments, string name)
        {
            for (int index = 0; index < arguments.Length; index++)
            {
                string argument = arguments[index];
                if (string.Equals(argument, name, StringComparison.Ordinal))
                {
                    if (index + 1 >= arguments.Length || arguments[index + 1].StartsWith("-", StringComparison.Ordinal))
                        throw new ArgumentException(name + " requires a file path");
                    return arguments[index + 1];
                }

                string prefix = name + "=";
                if (argument.StartsWith(prefix, StringComparison.Ordinal)) return argument.Substring(prefix.Length);
            }
            return null;
        }
    }
}
