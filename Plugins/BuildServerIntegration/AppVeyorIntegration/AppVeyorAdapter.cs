using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitCommands.Utils;
using GitUI;
using GitUIPluginInterfaces;
using GitUIPluginInterfaces.BuildServerIntegration;
using JetBrains.Annotations;
using Newtonsoft.Json.Linq;

namespace AppVeyorIntegration
{
    [MetadataAttribute]
    [AttributeUsage(AttributeTargets.Class)]
    public class AppVeyorIntegrationMetadata : BuildServerAdapterMetadataAttribute
    {
        public AppVeyorIntegrationMetadata(string buildServerType)
            : base(buildServerType)
        {
        }

        public override string CanBeLoaded
        {
            get
            {
                if (EnvUtils.IsNet4FullOrHigher())
                {
                    return null;
                }

                return ".Net 4 full framework required";
            }
        }
    }

    [Export(typeof(IBuildServerAdapter))]
    [AppVeyorIntegrationMetadata(PluginName)]
    [PartCreationPolicy(CreationPolicy.NonShared)]
    internal class AppVeyorAdapter : IBuildServerAdapter
    {
        public const string PluginName = "AppVeyor";
        private const uint ProjectsToRetrieveCount = 25;
        private const string WebSiteUrl = "https://ci.appveyor.com";
        private const string ApiBaseUrl = WebSiteUrl + "/api/projects/";

        private IBuildServerWatcher _buildServerWatcher;

        private HttpClient _httpClientAppVeyor;

        private List<AppVeyorBuildInfo> _allBuilds = new List<AppVeyorBuildInfo>();
        private HashSet<ObjectId> _fetchBuilds;
        private string _accountToken;
        private static readonly Dictionary<string, Project> Projects = new Dictionary<string, Project>();
        private Func<ObjectId, bool> _isCommitInRevisionGrid;
        private bool _shouldLoadTestResults;

        public void Initialize(
            IBuildServerWatcher buildServerWatcher,
            ISettingsSource config,
            Action openSettings,
            Func<ObjectId, bool> isCommitInRevisionGrid = null)
        {
            if (_buildServerWatcher is not null)
            {
                throw new InvalidOperationException("Already initialized");
            }

            _buildServerWatcher = buildServerWatcher;
            _isCommitInRevisionGrid = isCommitInRevisionGrid;
            var accountName = config.GetString("AppVeyorAccountName", null);
            _accountToken = config.GetString("AppVeyorAccountToken", null);
            var projectNamesSetting = config.GetString("AppVeyorProjectName", null);
            if (string.IsNullOrWhiteSpace(accountName) && string.IsNullOrWhiteSpace(projectNamesSetting))
            {
                return;
            }

            _shouldLoadTestResults = config.GetBool("AppVeyorLoadTestsResults", false);

            _fetchBuilds = new HashSet<ObjectId>();

            _httpClientAppVeyor = GetHttpClient(WebSiteUrl, _accountToken);

            var useAllProjects = string.IsNullOrWhiteSpace(projectNamesSetting);
            string[] projectNames = null;
            if (!useAllProjects)
            {
                projectNames = _buildServerWatcher.ReplaceVariables(projectNamesSetting)
                    .Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            }

            if (Projects.Count == 0 ||
                (!useAllProjects && Projects.Keys.Intersect(projectNames).Count() != projectNames.Length))
            {
                Projects.Clear();
                if (string.IsNullOrWhiteSpace(_accountToken))
                {
                    FillProjectsFromSettings(accountName, projectNames);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(accountName))
                    {
                        return;
                    }

                    ThreadHelper.JoinableTaskFactory.Run(
                        async () =>
                        {
                            var result = await GetResponseAsync(_httpClientAppVeyor, ApiBaseUrl, CancellationToken.None).ConfigureAwait(false);

                            if (string.IsNullOrWhiteSpace(result))
                            {
                                return;
                            }

                            var projects = JArray.Parse(result);
                            foreach (var project in projects)
                            {
                                var projectId = project["slug"].ToString();
                                projectId = accountName.Combine("/", projectId);
                                var projectName = project["name"].ToString();
                                var projectObj = new Project
                                {
                                    Name = projectName,
                                    Id = projectId,
                                    QueryUrl = BuildQueryUrl(projectId)
                                };

                                if (useAllProjects || projectNames.Contains(projectObj.Name))
                                {
                                    Projects[projectObj.Name] = projectObj;
                                }
                            }
                        });
                }
            }

            var builds = Projects.Where(p => useAllProjects || projectNames.Contains(p.Value.Name)).Select(p => p.Value);
            _allBuilds =
                FilterBuilds(builds.SelectMany(project => QueryBuildsResults(project)));
        }

        private static void FillProjectsFromSettings(string accountName, [InstantHandle] IEnumerable<string> projectNames)
        {
            foreach (var projectName in projectNames)
            {
                var projectId = accountName.Combine("/", projectName);
                if (!Projects.ContainsKey(projectName))
                {
                    Projects.Add(projectName, new Project
                    {
                        Name = projectName,
                        Id = projectId,
                        QueryUrl = BuildQueryUrl(projectId)
                    });
                }
            }
        }

        private static HttpClient GetHttpClient(string baseUrl, string accountToken)
        {
            var httpClient = new HttpClient(new HttpClientHandler { UseDefaultCredentials = true })
            {
                Timeout = TimeSpan.FromMinutes(2),
                BaseAddress = new Uri(baseUrl, UriKind.Absolute),
            };
            if (!string.IsNullOrWhiteSpace(accountToken))
            {
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accountToken);
            }

            return httpClient;
        }

        private static string BuildQueryUrl(string projectId)
        {
            return ApiBaseUrl + projectId + "/history?recordsNumber=" + ProjectsToRetrieveCount;
        }

        internal class Project
        {
            public string Name;
            public string Id;
            public string QueryUrl;
        }

        private string BuildPullRequetUrl(string repositoryType, string repositoryName, string pullRequestId)
        {
            switch (repositoryType.ToLowerInvariant())
            {
                case "bitbucket":
                    return $"https://bitbucket.org/{repositoryName}/pull-requests/{pullRequestId}";
                case "github":
                    return $"https://github.com/{repositoryName}/pull/{pullRequestId}";
                case "gitlab":
                    return $"https://gitlab.com/{repositoryName}/merge_requests/{pullRequestId}";
                case "vso":
                case "git":
                default:
                    return null;
            }
        }

        private IEnumerable<AppVeyorBuildInfo> QueryBuildsResults(Project project)
        {
            try
            {
                return ThreadHelper.JoinableTaskFactory.Run(
                    async () =>
                    {
                        var result = await GetResponseAsync(_httpClientAppVeyor, project.QueryUrl, CancellationToken.None).ConfigureAwait(false);

                        return ExtractBuildInfo(project, result);
                    });
            }
            catch
            {
                return Enumerable.Empty<AppVeyorBuildInfo>();
            }
        }

        internal IEnumerable<AppVeyorBuildInfo> ExtractBuildInfo(Project project, string result)
        {
            if (string.IsNullOrWhiteSpace(result))
            {
                return Enumerable.Empty<AppVeyorBuildInfo>();
            }

            var content = JObject.Parse(result);

            var projectData = content["project"];
            var repositoryName = projectData["repositoryName"];
            var repositoryType = projectData["repositoryType"];

            var builds = content["builds"].Children();
            var baseApiUrl = ApiBaseUrl + project.Id;
            var baseWebUrl = WebSiteUrl + "/project/" + project.Id + "/build/";

            var buildDetails = new List<AppVeyorBuildInfo>();
            foreach (var b in builds)
            {
                try
                {
                    if (!ObjectId.TryParse((b["pullRequestHeadCommitId"] ?? b["commitId"]).ToObject<string>(),
                            out var objectId) || !_isCommitInRevisionGrid(objectId))
                    {
                        continue;
                    }

                    var pullRequestId = b["pullRequestId"];
                    var version = b["version"].ToObject<string>();
                    var status = ParseBuildStatus(b["status"].ToObject<string>());
                    long? duration = null;
                    if (status is (BuildInfo.BuildStatus.Success or BuildInfo.BuildStatus.Failure))
                    {
                        duration = GetBuildDuration(b);
                    }

                    var pullRequestTitle = b["pullRequestName"];

                    buildDetails.Add(new AppVeyorBuildInfo
                    {
                        Id = version,
                        BuildId = b["buildId"].ToObject<string>(),
                        Branch = b["branch"].ToObject<string>(),
                        CommitId = objectId,
                        CommitHashList = new[] { objectId },
                        Status = status,
                        StartDate = b["started"]?.ToObject<DateTime>() ?? DateTime.MinValue,
                        BaseWebUrl = baseWebUrl,
                        Url = WebSiteUrl + "/project/" + project.Id + "/build/" + version,
                        PullRequestUrl = repositoryType is not null && repositoryName is not null && pullRequestId is not null
                            ? BuildPullRequetUrl(repositoryType.Value<string>(), repositoryName.Value<string>(),
                                pullRequestId.Value<string>())
                            : null,
                        BaseApiUrl = baseApiUrl,
                        AppVeyorBuildReportUrl = baseApiUrl + "/build/" + version,
                        PullRequestText = pullRequestId is not null ? "PR#" + pullRequestId.Value<string>() : string.Empty,
                        PullRequestTitle = pullRequestTitle is not null ? pullRequestTitle.Value<string>() : string.Empty,
                        Duration = duration,
                        TestsResultText = string.Empty
                    });
                }
                catch (Exception)
                {
                    // Failure on reading data on a build detail should not prevent to display the others build results
                }
            }

            return buildDetails;
        }

        /// <summary>
        /// Gets a unique key which identifies this build server.
        /// </summary>
        public string UniqueKey => _httpClientAppVeyor.BaseAddress.Host;

        public IObservable<BuildInfo> GetFinishedBuildsSince(IScheduler scheduler, DateTime? sinceDate = null)
        {
            // AppVeyor api is different than TeamCity one and all build results are fetch in one call without
            // filter parameters possible (so this call is useless!)
            return Observable.Empty<BuildInfo>();
        }

        public IObservable<BuildInfo> GetRunningBuilds(IScheduler scheduler)
        {
            return GetBuilds(scheduler);
        }

        private IObservable<BuildInfo> GetBuilds(IScheduler scheduler)
        {
            return Observable.Create<BuildInfo>((observer, cancellationToken) =>
                Task.Run(
                    () => scheduler.Schedule(() => ObserveBuilds(observer, cancellationToken))));
        }

        private void ObserveBuilds(IObserver<BuildInfo> observer, CancellationToken cancellationToken)
        {
            try
            {
                if (_allBuilds is null)
                {
                    return;
                }

                // Display all builds found
                foreach (var build in _allBuilds)
                {
                    UpdateDisplay(observer, build);
                }

                // Update finished build with tests results
                if (_shouldLoadTestResults)
                {
                    foreach (var build in _allBuilds.Where(b => b.Status == BuildInfo.BuildStatus.Success
                                                                || b.Status == BuildInfo.BuildStatus.Failure))
                    {
                        UpdateDescription(build, cancellationToken);
                        UpdateDisplay(observer, build);
                    }
                }

                // Manage in progress builds...
                var inProgressBuilds = _allBuilds.Where(b => b.Status == BuildInfo.BuildStatus.InProgress).ToList();
                _allBuilds = null;
                do
                {
                    Thread.Sleep(5000);
                    foreach (var build in inProgressBuilds)
                    {
                        UpdateDescription(build, cancellationToken);
                        UpdateDisplay(observer, build);
                    }

                    inProgressBuilds = inProgressBuilds.Where(b => b.Status == BuildInfo.BuildStatus.InProgress).ToList();
                }
                while (inProgressBuilds.Any());

                observer.OnCompleted();
            }
            catch (OperationCanceledException)
            {
                // Do nothing, the observer is already stopped
            }
            catch (Exception ex)
            {
                observer.OnError(ex);
            }
        }

        private static void UpdateDisplay(IObserver<BuildInfo> observer, AppVeyorBuildInfo build)
        {
            build.UpdateDescription();
            observer.OnNext(build);
        }

        private List<AppVeyorBuildInfo> FilterBuilds(IEnumerable<AppVeyorBuildInfo> allBuilds)
        {
            var filteredBuilds = new List<AppVeyorBuildInfo>();
            foreach (var build in allBuilds.OrderByDescending(b => b.StartDate))
            {
                if (!_fetchBuilds.Contains(build.CommitId))
                {
                    filteredBuilds.Add(build);
                    _fetchBuilds.Add(build.CommitId);
                }
            }

            return filteredBuilds;
        }

        private void UpdateDescription(AppVeyorBuildInfo buildDetails, CancellationToken cancellationToken)
        {
            var buildDetailsParsed = ThreadHelper.JoinableTaskFactory.Run(() => FetchBuildDetailsManagingVersionUpdateAsync(buildDetails, cancellationToken));
            if (buildDetailsParsed is null)
            {
                return;
            }

            var buildData = buildDetailsParsed["build"];
            var buildDescription = buildData["jobs"].Last();

            var status = buildDescription["status"].ToObject<string>();
            buildDetails.Status = ParseBuildStatus(status);

            buildDetails.ChangeProgressCounter();
            if (!buildDetails.IsRunning)
            {
                buildDetails.Duration = GetBuildDuration(buildData);
            }

            int testCount = buildDescription["testsCount"].ToObject<int>();
            if (testCount != 0)
            {
                int failedTestCount = buildDescription["failedTestsCount"].ToObject<int>();
                int skippedTestCount = testCount - buildDescription["passedTestsCount"].ToObject<int>();
                var testResults = testCount + " tests";
                if (failedTestCount != 0 || skippedTestCount != 0)
                {
                    testResults += $" ( {failedTestCount} failed, {skippedTestCount} skipped )";
                }

                buildDetails.TestsResultText = testResults;
            }
        }

        private static long GetBuildDuration(JToken buildData)
        {
            var startTime = (buildData["started"] ?? buildData["created"])?.ToObject<DateTime>();
            var updateTime = buildData["updated"]?.ToObject<DateTime>();
            if (!startTime.HasValue || !updateTime.HasValue)
            {
                return 0;
            }

            return (long)(updateTime.Value - startTime.Value).TotalMilliseconds;
        }

        private async Task<JObject> FetchBuildDetailsManagingVersionUpdateAsync(AppVeyorBuildInfo buildDetails, CancellationToken cancellationToken)
        {
            try
            {
                return JObject.Parse(await GetResponseAsync(_httpClientAppVeyor, buildDetails.AppVeyorBuildReportUrl, cancellationToken).ConfigureAwait(false));
            }
            catch
            {
                var buildHistoryUrl = buildDetails.BaseApiUrl + "/history?recordsNumber=1&startBuildId=" + (int.Parse(buildDetails.BuildId) + 1);
                var builds = JObject.Parse(await GetResponseAsync(_httpClientAppVeyor, buildHistoryUrl, cancellationToken).ConfigureAwait(false));

                var version = builds["builds"][0]["version"].ToObject<string>();
                buildDetails.Id = version;
                buildDetails.AppVeyorBuildReportUrl = buildDetails.BaseApiUrl + "/build/" + version;
                buildDetails.Url = buildDetails.BaseWebUrl + version;

                return JObject.Parse(await GetResponseAsync(_httpClientAppVeyor, buildDetails.AppVeyorBuildReportUrl, cancellationToken).ConfigureAwait(false));
            }
        }

        private static BuildInfo.BuildStatus ParseBuildStatus(string statusValue)
        {
            switch (statusValue)
            {
                case "success":
                    return BuildInfo.BuildStatus.Success;
                case "failed":
                    return BuildInfo.BuildStatus.Failure;
                case "cancelled":
                    return BuildInfo.BuildStatus.Stopped;
                case "queued":
                case "running":
                    return BuildInfo.BuildStatus.InProgress;
                default:
                    return BuildInfo.BuildStatus.Unknown;
            }
        }

        private Task<Stream> GetStreamAsync(HttpClient httpClient, string restServicePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return httpClient.GetAsync(restServicePath, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                             .ContinueWith(
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks (task is a completed task)
                                 task => GetStreamFromHttpResponseAsync(httpClient, task, restServicePath, cancellationToken),
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks
                                 cancellationToken,
                                 restServicePath.Contains("github") ? TaskContinuationOptions.None : TaskContinuationOptions.AttachedToParent,
                                 TaskScheduler.Current)
                             .Unwrap();
        }

        private Task<Stream> GetStreamFromHttpResponseAsync(HttpClient httpClient, Task<HttpResponseMessage> task, string restServicePath, CancellationToken cancellationToken)
        {
            var retry = task.IsCanceled && !cancellationToken.IsCancellationRequested;

            if (retry)
            {
                return GetStreamAsync(httpClient, restServicePath, cancellationToken);
            }

            if (task.Status == TaskStatus.RanToCompletion && task.CompletedResult().IsSuccessStatusCode)
            {
                return task.CompletedResult().Content.ReadAsStreamAsync();
            }

            return null;
        }

        private Task<string> GetResponseAsync(HttpClient httpClient, string relativePath, CancellationToken cancellationToken)
        {
            var getStreamTask = GetStreamAsync(httpClient, relativePath, cancellationToken);

            var taskContinuationOptions = relativePath.Contains("github") ? TaskContinuationOptions.None : TaskContinuationOptions.AttachedToParent;
            return getStreamTask.ContinueWith(
                task =>
                {
                    if (task.Status != TaskStatus.RanToCompletion)
                    {
                        return string.Empty;
                    }

                    using (var responseStream = task.Result)
                    {
                        return new StreamReader(responseStream).ReadToEnd();
                    }
                },
                cancellationToken,
                taskContinuationOptions,
                TaskScheduler.Current);
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);

            _httpClientAppVeyor?.Dispose();
        }
    }
}
