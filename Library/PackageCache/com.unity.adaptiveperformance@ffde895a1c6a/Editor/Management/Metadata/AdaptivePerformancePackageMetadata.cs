using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using UnityEngine;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine.AdaptivePerformance;

namespace UnityEditor.AdaptivePerformance.Editor.Metadata
{
    /// <summary>
    /// Provides an interface for describing specific loader metadata. Package authors should implement
    /// this interface for each loader they provide in their package.
    /// </summary>
    public interface IAdaptivePerformanceLoaderMetadata
    {
        /// <summary>
        /// The user-facing name for this loader. Will be used to populate the
        /// list in the Adaptive Performance Provider Management UI.
        /// </summary>
        string loaderName { get; }

        /// <summary>
        /// The full type name for this loader. This is used to allow management to find and
        /// create instances of supported loaders for your package.
        ///
        /// When your package is first installed, the Adaptive Performance Provider Management system will
        /// use this information to create instances of your loaders in Assets/Adaptive Performance/Loaders.
        /// </summary>
        string loaderType { get; }

        /// <summary>
        /// The full list of supported build targets for this loader. This allows the UI to only show the
        /// loaders appropriate for a specific build target.
        ///
        /// Returning an empty list or a list containing just <a href="https://docs.unity3d.com/ScriptReference/BuildTargetGroup.Unknown.html">BuildTargetGroup.Unknown</a> will make this
        /// loader invisible in the UI.
        /// </summary>
        List<BuildTargetGroup> supportedBuildTargets { get; }
    }

    /// <summary>
    /// Top-level package metadata interface. Create an instance of this interface to
    /// provide metadata information for your package.
    /// </summary>
    public interface IAdaptivePerformancePackageMetadata
    {
        /// <summary>
        /// User-facing package name. Should be the same as the value for the
        /// displayName keyword in the package.json file.
        /// </summary>
        string packageName { get; }

        /// <summary>
        /// The package id used to track and install the package. Must be the same value
        /// as the name keyword in the package.json file, otherwise installation will
        /// not be possible.
        /// </summary>
        string packageId { get; }

        /// <summary>
        /// The full type name for the settings type for your package.
        ///
        /// When your package is first installed, the Adaptive Performance Provider Management system will
        /// use this information to create an instance of your settings in Assets/Adaptive Performance/Settings.
        /// </summary>
        string settingsType { get; }

        /// <summary>
        /// The full URL for the license for your package.
        ///
        /// The full URL is used to inform the user that a separate package license exists. Must point to the same
        /// license as the one in the `package.json` file which displays in the provider list. The user accepts the license
        /// by installing the provider.
        /// </summary>
        string licenseURL { get; }

        /// <summary>
        /// List of <see cref="IAdaptivePerformanceLoaderMetadata"/> instances describing the data about the loaders
        /// your package supports.
        /// </summary>
        List<IAdaptivePerformanceLoaderMetadata> loaderMetadata { get; }
    }


    /// <summary>
    /// Provide access to the metadata store. Currently only usable as a way to assign and remove loaders
    /// to or from an <see cref="AdaptivePerformanceManagerSettings"/> instance.
    /// </summary>
    [InitializeOnLoad]
    public class AdaptivePerformancePackageMetadataStore
    {
        const string k_WaitingPackmanQuery = "APMGT Waiting Packman Query.";
        const string k_RebuildCache = "APMGT Rebuilding Cache.";
        const string k_InstallingPackage = "APMGT Installing Adaptive Performance Package.";
        const string k_AssigningPackage = "APMGT Assigning Adaptive Performance Package.";
        const string k_UninstallingPackage = "APMGT Uninstalling Adaptive Performance Package.";
        const string k_CachedMDStoreKey = "Adaptive Performance Metadata Store";

        static float k_TimeOutDelta = 30f;

        [Serializable]
        struct KnownPackageInfo
        {
            public string packageId;
            public string verifiedVersion;
        }


        [Serializable]
        struct CachedMDStoreInformation
        {
            public bool hasAlreadyRequestedData;
            public KnownPackageInfo[] knownPackageInfos;
            public string[] installedPackages;
            public string[] installablePackages;
        }

        static CachedMDStoreInformation s_CachedMDStoreInformation = new CachedMDStoreInformation()
        {
            hasAlreadyRequestedData = false,
            knownPackageInfos = {},
            installedPackages = {},
            installablePackages = {},
        };


        static void LoadCachedMDStoreInformation()
        {
            string data = SessionState.GetString(k_CachedMDStoreKey, "{}");
            s_CachedMDStoreInformation = JsonUtility.FromJson<CachedMDStoreInformation>(data);
        }

        static void StoreCachedMDStoreInformation()
        {
            SessionState.EraseString(k_CachedMDStoreKey);
            string data = JsonUtility.ToJson(s_CachedMDStoreInformation, true);
            SessionState.SetString(k_CachedMDStoreKey, data);
        }

        enum InstallationState
        {
            New,
            RebuildInstalledCache,
            StartInstallation,
            Installing,
            Assigning,
            Complete,
            Uninstalling,
            Log
        }

        enum LogLevel
        {
            Info,
            Warning,
            Error
        }

        [Serializable]
        struct LoaderAssignmentRequest
        {
            [SerializeField]
            public string packageId;
            [SerializeField]
            public string loaderType;
            [SerializeField]
            public BuildTargetGroup buildTargetGroup;
            [SerializeField]
            public bool needsAddRequest;
            [SerializeField]
            public ListRequest packageListRequest;
            [SerializeField]
            public AddRequest packageAddRequest;
            [SerializeField]
#pragma warning disable CS0649
            public RemoveRequest packageRemoveRequest;
#pragma warning restore CS0649
            [SerializeField]
            public float timeOut;
            [SerializeField]
            public InstallationState installationState;
            [SerializeField]
            public string logMessage;
            [SerializeField]
            public LogLevel logLevel;
        }

        [Serializable]
        struct LoaderAssignmentRequests
        {
            [SerializeField]
            public List<LoaderAssignmentRequest> activeRequests;
        }

        static List<LoaderAssignmentRequest> m_AddRequests = new List<LoaderAssignmentRequest>();
        static Dictionary<string, IAdaptivePerformancePackage> s_Packages = new Dictionary<string, IAdaptivePerformancePackage>();
        static SearchRequest s_SearchRequest = null;

        const string k_DefaultSessionStateString = "AP_DEFAULT_SESSION_STATE";
        static bool SessionStateHasStoredData(string queueName)
        {
            return SessionState.GetString(queueName, k_DefaultSessionStateString) != k_DefaultSessionStateString;
        }

        internal static bool isCheckingInstallationRequirements => SessionStateHasStoredData(k_WaitingPackmanQuery);
        internal static bool isRebuildingCache => SessionStateHasStoredData(k_RebuildCache);
        internal static bool isInstallingPackages => SessionStateHasStoredData(k_InstallingPackage);
        internal static bool isUninstallingPackages => SessionStateHasStoredData(k_UninstallingPackage);
        internal static bool isAssigningLoaders => SessionStateHasStoredData(k_AssigningPackage);

        internal static bool isDoingQueueProcessing
        {
            get
            {
                return isCheckingInstallationRequirements || isRebuildingCache || isInstallingPackages || isUninstallingPackages || isAssigningLoaders;
            }
        }


        internal struct LoaderBuildTargetQueryResult
        {
            public string packageName;
            public string packageId;
            public string loaderName;
            public string loaderType;
            public string licenseURL;
        }

        internal static List<LoaderBuildTargetQueryResult> GetAllLoadersForBuildTarget(BuildTargetGroup buildTarget)
        {
            var ret = from pm in (from p in s_Packages.Values select p.metadata)
                from lm in pm.loaderMetadata
                where lm.supportedBuildTargets.Contains(buildTarget)
                orderby lm.loaderName
                select new LoaderBuildTargetQueryResult() { packageName = pm.packageName, packageId = pm.packageId, loaderName = lm.loaderName, loaderType = lm.loaderType };
            var retList = ret.Distinct().ToList<LoaderBuildTargetQueryResult>();
            return retList;
        }

        internal static List<LoaderBuildTargetQueryResult> GetLoadersForBuildTarget(BuildTargetGroup buildTargetGroup)
        {
            var ret = from pm in (from p in s_Packages.Values select p.metadata)
                from lm in pm.loaderMetadata
                where lm.supportedBuildTargets.Contains(buildTargetGroup)
                orderby lm.loaderName
                select new LoaderBuildTargetQueryResult() { packageName = pm.packageName, packageId = pm.packageId, loaderName = lm.loaderName, loaderType = lm.loaderType, licenseURL = pm.licenseURL };
            var retList = ret.ToList<LoaderBuildTargetQueryResult>();
            return retList;
        }

        internal static IAdaptivePerformancePackageMetadata GetMetadataForPackage(string packageId)
        {
            return s_Packages.Values.
                Select(x => x.metadata).
                FirstOrDefault(xmd => String.Compare(xmd.packageId, packageId) == 0);
        }

        internal static bool HasInstallablePackageData()
        {
            return s_CachedMDStoreInformation.installablePackages?.Any() ?? false;
        }

        internal static bool IsPackageInstalled(string package)
        {
            return (s_CachedMDStoreInformation.installedPackages?.Contains(package) ?? false)
                && File.Exists($"Packages/{package}/package.json");
        }

        internal static bool IsPackageInstallable(string package)
        {
            return s_CachedMDStoreInformation.installablePackages?.Contains(package) ?? false;
        }

        internal static bool IsLoaderAssigned(string loaderTypeName, BuildTargetGroup buildTargetGroup)
        {
            var settings = AdaptivePerformanceGeneralSettingsPerBuildTarget.AdaptivePerformanceGeneralSettingsForBuildTarget(buildTargetGroup);
            if (settings == null)
                return false;

            foreach (var loader in settings.AssignedSettings.loaders)
            {
                if (loader != null && String.Compare(loader.GetType().FullName, loaderTypeName) == 0)
                    return true;
            }
            return false;
        }

        internal static bool IsLoaderAssigned(AdaptivePerformanceManagerSettings settings, string loaderTypeName)
        {
            if (settings == null)
                return false;

            foreach (var l in settings.loaders)
            {
                if (l != null && String.Compare(l.GetType().FullName, loaderTypeName) == 0)
                    return true;
            }
            return false;
        }

        internal static void InstallPackageAndAssignLoaderForBuildTarget(string package, string loaderType, BuildTargetGroup buildTargetGroup)
        {
            var req = new LoaderAssignmentRequest();
            req.packageId = package;
            req.loaderType = loaderType;
            req.buildTargetGroup = buildTargetGroup;
            req.installationState = InstallationState.New;
            QueueLoaderRequest(req);
        }

        /// <summary>
        /// Assigns a loader of type loaderTypeName to the settings instance. Will instantiate an
        /// instance if one can't be found in the user's project folder before assigning it.
        /// </summary>
        /// <param name="settings">An instance of <see cref="AdaptivePerformanceManagerSettings"/> to add the loader to.</param>
        /// <param name="loaderTypeName">The full type name for the loader instance to assign to settings.</param>
        /// <param name="buildTargetGroup">The build target group being assigned to.</param>
        /// <returns>True if assignment succeeds, false if not.</returns>
        public static bool AssignLoader(AdaptivePerformanceManagerSettings settings, string loaderTypeName, BuildTargetGroup buildTargetGroup)
        {
            var instance = EditorUtilities.GetInstanceOfTypeWithNameFromAssetDatabase(loaderTypeName);
            if (instance == null || !(instance is AdaptivePerformanceLoader))
            {
                instance = EditorUtilities.CreateScriptableObjectInstance(loaderTypeName,
                    EditorUtilities.GetAssetPathForComponents(EditorUtilities.s_DefaultLoaderPath));
                if (instance == null)
                    return false;
            }

            var assignedLoaders = settings.loaders;
            AdaptivePerformanceLoader newLoader = instance as AdaptivePerformanceLoader;

            if (!assignedLoaders.Contains(newLoader))
            {
                assignedLoaders.Add(newLoader);
                settings.loaders = new List<AdaptivePerformanceLoader>();

                var allLoaders = GetAllLoadersForBuildTarget(buildTargetGroup);

                foreach (var ldr in allLoaders)
                {
                    var newInstance = EditorUtilities.GetInstanceOfTypeWithNameFromAssetDatabase(ldr.loaderType) as AdaptivePerformanceLoader;

                    if (newInstance != null && assignedLoaders.Contains(newInstance))
                    {
                        settings.loaders.Add(newInstance);
#if UNITY_EDITOR
                        var loaderHelper = newLoader as AdaptivePerformanceLoaderHelper;
                        loaderHelper?.WasAssignedToBuildTarget(buildTargetGroup);
#endif
                    }
                }

                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
            }

            return true;
        }

        /// <summary>
        /// Remove a previously assigned loader from settings. If the loader type is unknown or
        /// an instance of the loader can't be found in the project folder, no action is taken.
        ///
        /// Removal will not delete the instance from the project folder.
        /// </summary>
        /// <param name="settings">An instance of <see cref="AdaptivePerformanceManagerSettings"/> to add the loader to.</param>
        /// <param name="loaderTypeName">The full type name for the loader instance to remove from settings.</param>
        /// <param name="buildTargetGroup">The build target group being removed from.</param>
        /// <returns>True if removal succeeds, false if not.</returns>
        public static bool RemoveLoader(AdaptivePerformanceManagerSettings settings, string loaderTypeName, BuildTargetGroup buildTargetGroup)
        {
            var instance = EditorUtilities.GetInstanceOfTypeWithNameFromAssetDatabase(loaderTypeName);
            if (instance == null || !(instance is AdaptivePerformanceLoader))
                return false;

            AdaptivePerformanceLoader loader = instance as AdaptivePerformanceLoader;

            if (settings.loaders.Contains(loader))
            {
                settings.loaders.Remove(loader);
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
#if UNITY_EDITOR
                var loaderHelper = loader as AdaptivePerformanceLoaderHelper;
                loaderHelper?.WasUnassignedFromBuildTarget(buildTargetGroup);
#endif
            }

            return true;
        }

        internal static IAdaptivePerformancePackage GetPackageForSettingsTypeNamed(string settingsTypeName)
        {
            var ret = s_Packages.Values.
                Where((p => String.Compare(p.metadata.settingsType, settingsTypeName, true) == 0)).
                Select((p) => p);
            return ret.Any() ? ret.First() : null;
        }

        internal static string GetCurrentStatusDisplayText()
        {
            if (AdaptivePerformancePackageMetadataStore.isCheckingInstallationRequirements)
            {
                return "Checking installation requirements for packages...";
            }
            else if (AdaptivePerformancePackageMetadataStore.isRebuildingCache)
            {
                return "Querying Package Manager for currently installed packages...";
            }
            else if (AdaptivePerformancePackageMetadataStore.isInstallingPackages)
            {
                return "Installing packages...";
            }
            else if (AdaptivePerformancePackageMetadataStore.isUninstallingPackages)
            {
                return "Uninstalling packages...";
            }
            else if (AdaptivePerformancePackageMetadataStore.isAssigningLoaders)
            {
                return "Assigning all requested loaders...";
            }

            return "";
        }

        internal static void AddPluginPackage(IAdaptivePerformancePackage package)
        {
            if (s_CachedMDStoreInformation.installedPackages != null && !s_CachedMDStoreInformation.installedPackages.Contains(package.metadata.packageId))
            {
                List<string> installedPackages = s_CachedMDStoreInformation.installedPackages.ToList<string>();
                installedPackages.Add(package.metadata.packageId);
                s_CachedMDStoreInformation.installedPackages = installedPackages.ToArray();
                StoreCachedMDStoreInformation();
            }
            InternalAddPluginPackage(package);
        }

        static void InternalAddPluginPackage(IAdaptivePerformancePackage package)
        {
            s_Packages[package.metadata.packageId] = package;
        }

        internal static void InitKnownPluginPackages()
        {
            foreach (var knownPackage in AdaptivePerformanceKnownPackages.Packages)
            {
                InternalAddPluginPackage(knownPackage);
            }
        }

        static AdaptivePerformancePackageMetadataStore()
        {
            InitKnownPluginPackages();

            EditorApplication.playModeStateChanged += PlayModeStateChanged;

            if (IsEditorInPlayMode())
                return;

            AssemblyReloadEvents.afterAssemblyReload += AssemblyReloadEvents_afterAssemblyReload;
        }

        static void AssemblyReloadEvents_afterAssemblyReload()
        {
            LoadCachedMDStoreInformation();

            if (!IsEditorInPlayMode())
            {
                if (!s_CachedMDStoreInformation.hasAlreadyRequestedData)
                {
                    s_SearchRequest = Client.SearchAll(true);
                }

                RebuildInstalledCache();
                StartAllQueues();
            }
        }

        static bool IsEditorInPlayMode()
        {
            return EditorApplication.isPlayingOrWillChangePlaymode ||
                EditorApplication.isPlaying ||
                EditorApplication.isPaused;
        }

        static void PlayModeStateChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.ExitingEditMode:
                    StopAllQueues();
                    StoreCachedMDStoreInformation();
                    break;

                case PlayModeStateChange.EnteredPlayMode:
                    break;

                case PlayModeStateChange.EnteredEditMode:
                    LoadCachedMDStoreInformation();
                    StartAllQueues();
                    break;
            }
        }

        static void StopAllQueues()
        {
            EditorApplication.update -= UpdateInstallablePackages;
            EditorApplication.update -= WaitingOnSearchQuery;
            EditorApplication.update -= MonitorPackageInstallation;
            EditorApplication.update -= MonitorPackageUninstall;
            EditorApplication.update -= AssignAnyRequestedLoadersUpdate;
            EditorApplication.update -= RebuildCache;
        }

        static void StartAllQueues()
        {
            EditorApplication.update += UpdateInstallablePackages;
            EditorApplication.update += WaitingOnSearchQuery;
            EditorApplication.update += MonitorPackageInstallation;
            EditorApplication.update += MonitorPackageUninstall;
            EditorApplication.update += AssignAnyRequestedLoadersUpdate;
            EditorApplication.update += RebuildCache;
        }

        static void UpdateInstallablePackages()
        {
            EditorApplication.update -= UpdateInstallablePackages;

            if (s_SearchRequest == null || IsEditorInPlayMode() || s_CachedMDStoreInformation.hasAlreadyRequestedData)
            {
                return;
            }

            if (!s_SearchRequest.IsCompleted)
            {
                EditorApplication.update += UpdateInstallablePackages;
                return;
            }

            var installablePackages = new List<string>();
            var knownPackageInfos = new List<KnownPackageInfo>();

            foreach (var package in s_SearchRequest.Result)
            {
                if (s_Packages.ContainsKey(package.name))
                {
                    var kpi = new KnownPackageInfo();
                    kpi.packageId = package.name;

                    kpi.verifiedVersion = package.versions.recommended;
                    if (string.IsNullOrEmpty(kpi.verifiedVersion) || kpi.verifiedVersion.StartsWith("1."))
                        kpi.verifiedVersion = package.versions.latestCompatible;
                    knownPackageInfos.Add(kpi);
                    installablePackages.Add(package.name);
                }
            }

            s_CachedMDStoreInformation.knownPackageInfos = knownPackageInfos.ToArray();
            s_CachedMDStoreInformation.installablePackages = installablePackages.ToArray();
            s_CachedMDStoreInformation.hasAlreadyRequestedData = true;

            s_SearchRequest = null;

            StoreCachedMDStoreInformation();
        }

        static void AddRequestToQueue(LoaderAssignmentRequest request, string queueName)
        {
            LoaderAssignmentRequests reqs;

            if (SessionStateHasStoredData(queueName))
            {
                string fromJson = SessionState.GetString(queueName, k_DefaultSessionStateString);
                reqs = JsonUtility.FromJson<LoaderAssignmentRequests>(fromJson);
            }
            else
            {
                reqs = new LoaderAssignmentRequests();
                reqs.activeRequests = new List<LoaderAssignmentRequest>();
            }

            reqs.activeRequests.Add(request);
            string json = JsonUtility.ToJson(reqs);
            SessionState.SetString(queueName, json);
        }

        static void SetRequestsInQueue(LoaderAssignmentRequests reqs, string queueName)
        {
            string json = JsonUtility.ToJson(reqs);
            SessionState.SetString(queueName, json);
        }

        static LoaderAssignmentRequests GetAllRequestsInQueue(string queueName)
        {
            var reqs = new LoaderAssignmentRequests();
            reqs.activeRequests = new List<LoaderAssignmentRequest>();

            if (SessionStateHasStoredData(queueName))
            {
                string fromJson = SessionState.GetString(queueName, k_DefaultSessionStateString);
                reqs = JsonUtility.FromJson<LoaderAssignmentRequests>(fromJson);
                SessionState.EraseString(queueName);
            }

            return reqs;
        }

        internal static void RebuildInstalledCache()
        {
            if (isRebuildingCache)
                return;

            var req = new LoaderAssignmentRequest();
            req.packageListRequest = Client.List(true, false);
            req.installationState = InstallationState.RebuildInstalledCache;
            req.timeOut = Time.realtimeSinceStartup + k_TimeOutDelta;
            QueueLoaderRequest(req);
        }

        static void RebuildCache()
        {
            EditorApplication.update -= RebuildCache;

            if (IsEditorInPlayMode())
            {
                return; // Use the cached data that should have been passed in the play state change.
            }

            LoaderAssignmentRequests reqs = GetAllRequestsInQueue(k_RebuildCache);

            if (reqs.activeRequests == null || reqs.activeRequests.Count == 0)
            {
                return;
            }

            var req = reqs.activeRequests[0];
            reqs.activeRequests.Remove(req);

            if (req.timeOut < Time.realtimeSinceStartup)
            {
                req.logMessage = $"Timeout trying to get package list after {k_TimeOutDelta}s.";
                req.logLevel = LogLevel.Warning;
                req.installationState = InstallationState.Log;
                QueueLoaderRequest(req);
            }
            else if (req.packageListRequest.IsCompleted)
            {
                if (req.packageListRequest.Status == StatusCode.Success)
                {
                    var installedPackages = new List<string>();

                    foreach (var packageInfo in req.packageListRequest.Result)
                    {
                        installedPackages.Add(packageInfo.name);
                    }

                    var packageIds = s_Packages.Values.
                        Where((p) => installedPackages.Contains(p.metadata.packageId)).
                        Select((p) => p.metadata.packageId);
                    s_CachedMDStoreInformation.installedPackages = packageIds.ToArray();
                }

                StoreCachedMDStoreInformation();
            }
            else if (!req.packageListRequest.IsCompleted)
            {
                QueueLoaderRequest(req);
            }
            else
            {
                req.logMessage = $"Unable to rebuild installed package cache. Some state may be missing or incorrect.";
                req.logLevel = LogLevel.Warning;
                req.installationState = InstallationState.Log;
                QueueLoaderRequest(req);
            }

            if (reqs.activeRequests.Count > 0)
            {
                SetRequestsInQueue(reqs, k_RebuildCache);
                EditorApplication.update += RebuildCache;
            }
        }

        static void ResetManagerUiIfAvailable()
        {
            if (AdaptivePerformanceSettingsManager.Instance != null) AdaptivePerformanceSettingsManager.Instance.ResetUi = true;
        }

        static void AssignAnyRequestedLoadersUpdate()
        {
            EditorApplication.update -= AssignAnyRequestedLoadersUpdate;

            LoaderAssignmentRequests reqs = GetAllRequestsInQueue(k_AssigningPackage);

            if (reqs.activeRequests == null || reqs.activeRequests.Count == 0)
                return;

            while (reqs.activeRequests.Count > 0)
            {
                var req = reqs.activeRequests[0];
                reqs.activeRequests.RemoveAt(0);

                var settings = AdaptivePerformanceGeneralSettingsPerBuildTarget.AdaptivePerformanceGeneralSettingsForBuildTarget(req.buildTargetGroup);

                if (settings == null)
                    continue;

                if (settings.AssignedSettings == null)
                {
                    var assignedSettings = ScriptableObject.CreateInstance<AdaptivePerformanceManagerSettings>() as AdaptivePerformanceManagerSettings;
                    settings.AssignedSettings = assignedSettings;
                    EditorUtility.SetDirty(settings);
                }

                if (!AdaptivePerformancePackageMetadataStore.AssignLoader(settings.AssignedSettings, req.loaderType, req.buildTargetGroup))
                {
                    req.installationState = InstallationState.Log;
                    req.logMessage = $"Unable to assign {req.packageId} for build target {req.buildTargetGroup}.";
                    req.logLevel = LogLevel.Error;
                    QueueLoaderRequest(req);
                }
            }

            ResetManagerUiIfAvailable();
        }

        internal static void AssignAnyRequestedLoaders()
        {
            EditorApplication.update += AssignAnyRequestedLoadersUpdate;
        }

        static void MonitorPackageInstallation()
        {
            EditorApplication.update -= MonitorPackageInstallation;
            LoaderAssignmentRequests reqs = GetAllRequestsInQueue(k_InstallingPackage);

            if (reqs.activeRequests.Count > 0)
            {
                var request = reqs.activeRequests[0];
                reqs.activeRequests.RemoveAt(0);

                if (request.needsAddRequest)
                {
                    var versionToInstallQ = s_CachedMDStoreInformation.knownPackageInfos.
                        Where((kpi) => String.Compare(request.packageId, kpi.packageId) == 0).
                        Select((kpi) => kpi.verifiedVersion);
                    var versionToInstall = versionToInstallQ.FirstOrDefault();
                    var packageToInstall = String.IsNullOrEmpty(versionToInstall) ?
                        request.packageId :
                        $"{request.packageId}@{versionToInstall}";
                    request.packageAddRequest = Client.Add(packageToInstall);
                    request.needsAddRequest = false;
                    request.installationState = InstallationState.Installing;

                    s_CachedMDStoreInformation.hasAlreadyRequestedData = true;
                    StoreCachedMDStoreInformation();

                    QueueLoaderRequest(request);
                }
                else if (request.packageAddRequest.IsCompleted && File.Exists($"Packages/{request.packageId}/package.json"))
                {
                    if (request.packageAddRequest.Status == StatusCode.Success)
                    {
                        if (!String.IsNullOrEmpty(request.loaderType))
                        {
                            request.packageAddRequest = null;
                            request.installationState = InstallationState.Assigning;
                            QueueLoaderRequest(request);
                        }
                        else
                        {
                            request.logMessage = $"Missing loader type. Unable to assign loader.";
                            request.logLevel = LogLevel.Error;
                            request.installationState = InstallationState.Log;
                            QueueLoaderRequest(request);
                        }
                    }
                }
                else if (request.packageAddRequest.IsCompleted && request.packageAddRequest.Status != StatusCode.Success)
                {
                    if (String.IsNullOrEmpty(request.packageId))
                    {
                        request.logMessage = $"Error installing package with no package id.";
                    }
                    else
                    {
                        request.logMessage = $"Error Message: {request.packageAddRequest?.Error?.message ?? "UNKNOWN" }.\nError installing package {request.packageId ?? "UNKNOWN PACKAGE ID" }.";
                    }

                    request.logLevel = LogLevel.Error;
                    request.installationState = InstallationState.Log;
                    QueueLoaderRequest(request);
                }
                else if (request.timeOut < Time.realtimeSinceStartup)
                {
                    if (String.IsNullOrEmpty(request.packageId))
                    {
                        request.logMessage = $"Time out while installing pacakge with no package id.";
                    }
                    else
                    {
                        request.logMessage = $"Error installing package {request.packageId}. Package installation timed out. Check Package Manager UI to see if the package is installed and/or retry your operation.";
                    }

                    request.logLevel = LogLevel.Error;

                    if (request.packageAddRequest.IsCompleted)
                    {
                        request.logMessage += $" Error message: {request.packageAddRequest.Error.message}";
                    }

                    request.installationState = InstallationState.Log;
                    QueueLoaderRequest(request);
                }
                else
                {
                    QueueLoaderRequest(request);
                }
            }
        }

        static void WaitingOnSearchQuery()
        {
            EditorApplication.update -= WaitingOnSearchQuery;
            if (s_SearchRequest != null)
            {
                EditorApplication.update += WaitingOnSearchQuery;
                return;
            }

            LoaderAssignmentRequests reqs = GetAllRequestsInQueue(k_WaitingPackmanQuery);
            if (reqs.activeRequests.Count > 0)
            {
                for (int i = 0; i < reqs.activeRequests.Count; i++)
                {
                    var req = reqs.activeRequests[i];
                    req.installationState = IsPackageInstalled(req.packageId) ? InstallationState.Assigning : InstallationState.StartInstallation;
                    req.timeOut = Time.realtimeSinceStartup + k_TimeOutDelta;
                    QueueLoaderRequest(req);
                }
            }
        }

        static void MonitorPackageUninstall()
        {
            EditorApplication.update -= MonitorPackageUninstall;
            LoaderAssignmentRequests reqs = GetAllRequestsInQueue(k_UninstallingPackage);
            if (reqs.activeRequests.Count > 0)
            {
                for (int i = 0; i < reqs.activeRequests.Count; i++)
                {
                    var req = reqs.activeRequests[i];
                    if (!req.packageRemoveRequest.IsCompleted)
                        QueueLoaderRequest(req);

                    if (req.packageRemoveRequest.Status == StatusCode.Failure)
                    {
                        req.installationState = InstallationState.Log;
                        req.logMessage = req.packageRemoveRequest.Error.message;
                        req.logLevel = LogLevel.Warning;
                        QueueLoaderRequest(req);
                    }
                }
            }
        }

        static void QueueLoaderRequest(LoaderAssignmentRequest req)
        {
            switch (req.installationState)
            {
                case InstallationState.New:
                    if (!s_CachedMDStoreInformation.hasAlreadyRequestedData && !HasInstallablePackageData() && s_SearchRequest == null)
                    {
                        s_SearchRequest = Client.SearchAll(false);
                        EditorApplication.update += UpdateInstallablePackages;
                    }
                    AddRequestToQueue(req, k_WaitingPackmanQuery);
                    EditorApplication.update += WaitingOnSearchQuery;
                    break;

                case InstallationState.RebuildInstalledCache:
                    AddRequestToQueue(req, k_RebuildCache);
                    EditorApplication.update += RebuildCache;
                    break;

                case InstallationState.StartInstallation:
                    req.needsAddRequest = true;
                    req.packageAddRequest = null;
                    req.timeOut = Time.realtimeSinceStartup + k_TimeOutDelta;
                    AddRequestToQueue(req, k_InstallingPackage);
                    EditorApplication.update += MonitorPackageInstallation;
                    break;

                case InstallationState.Installing:
                    AddRequestToQueue(req, k_InstallingPackage);
                    EditorApplication.update += MonitorPackageInstallation;
                    break;

                case InstallationState.Assigning:
                    AddRequestToQueue(req, k_AssigningPackage);
                    EditorApplication.update += AssignAnyRequestedLoadersUpdate;
                    break;

                case InstallationState.Uninstalling:
                    AddRequestToQueue(req, k_UninstallingPackage);
                    EditorApplication.update += MonitorPackageUninstall;
                    break;

                case InstallationState.Log:
                    const string header = "Adaptive Performance Provider Management";
                    switch (req.logLevel)
                    {
                        case LogLevel.Info:
                            Debug.Log($"{header}: {req.logMessage}");
                            break;

                        case LogLevel.Warning:
                            Debug.LogWarning($"{header} Warning: {req.logMessage}");
                            break;

                        case LogLevel.Error:
                            Debug.LogError($"{header} error. Failure reason: {req.logMessage}.\n Check if there are any other errors in the console and make sure they are corrected before trying again.");
                            break;
                    }
                    ResetManagerUiIfAvailable();
                    break;
            }
        }
    }
}
