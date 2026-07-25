using System.Text.Json;
using System.Text.Json.Serialization;
using GitKeyRouter.Core.Abstractions;
using GitKeyRouter.Core.Models;

namespace GitKeyRouter.Core.Services;

public sealed class GitUrlRewriteService
{
    private static readonly JsonSerializerOptions ConfigCloneOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IAppConfigStore _configStore;
    private readonly IGitUrlRewriteStore _store;
    private readonly IBackupService _backupService;
    private readonly GitProviderAdapterRegistry _providers;
    private readonly GitRemoteUrlParser _remoteUrlParser;

    public GitUrlRewriteService(
        IAppConfigStore configStore,
        IGitUrlRewriteStore store,
        IBackupService backupService,
        GitProviderAdapterRegistry? providers = null)
    {
        _configStore = configStore;
        _store = store;
        _backupService = backupService;
        _providers = providers ?? GitProviderAdapterRegistry.CreateDefault();
        _remoteUrlParser = new GitRemoteUrlParser(_providers);
    }

    public async Task<IReadOnlyList<GitRewriteComparison>> CompareAsync(CancellationToken cancellationToken = default)
    {
        var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var expectedEntries = BuildExpectedEntries(config);
        var expected = expectedEntries.Select(item => item.Rule).ToList();
        var actual = await _store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var legacyRules = FindLegacyAccountOwnerRules(config, actual);
        var comparisons = new List<GitRewriteComparison>();

        foreach (var entry in expectedEntries)
        {
            var expectedRule = entry.Rule;
            var exact = actual.Where(item => RuleEquals(item, expectedRule)).ToList();
            var samePrefix = actual.Where(item => string.Equals(item.InsteadOfUrl, expectedRule.InsteadOfUrl, StringComparison.OrdinalIgnoreCase)).ToList();
            var status = IsLegacyAccountOwnerEntry(entry)
                ? GitRewriteStatus.LegacyAccountOwner
                : exact.Count switch
                {
                    0 when samePrefix.Count > 0 => GitRewriteStatus.Conflict,
                    0 => GitRewriteStatus.Missing,
                    1 when samePrefix.Count == 1 => GitRewriteStatus.Correct,
                    _ => GitRewriteStatus.Duplicate
                };

            comparisons.Add(new GitRewriteComparison
            {
                ServiceInstanceId = entry.Route.ServiceInstanceId,
                NamespacePath = entry.Route.NamespacePath,
                GitHubOwner = entry.Service.ProviderKind == GitProviderKind.GitHub ? entry.Route.NamespacePath : null,
                IdentityId = entry.Identity.Id,
                IdentityDisplayName = entry.Identity.DisplayName,
                ExpectedBaseUrl = expectedRule.BaseUrl,
                InsteadOfUrl = expectedRule.InsteadOfUrl,
                Status = status,
                ActualMatchCount = exact.Count,
                ActualRules = samePrefix
            });
        }

        foreach (var extra in actual.Where(item => !expected.Any(expectedRule => RuleEquals(item, expectedRule))))
        {
            comparisons.Add(new GitRewriteComparison
            {
                ExpectedBaseUrl = extra.BaseUrl,
                InsteadOfUrl = extra.InsteadOfUrl,
                Status = legacyRules.Any(item => RuleEquals(item, extra))
                    ? GitRewriteStatus.LegacyAccountOwner
                    : GitRewriteStatus.Extra,
                ActualMatchCount = 1,
                ActualRules = [extra]
            });
        }

        return comparisons;
    }

    public Task<IReadOnlyList<GitUrlRewriteRule>> GetActualRulesAsync(CancellationToken cancellationToken = default)
        => _store.GetAllAsync(cancellationToken);

    public Task<OperationResult<IReadOnlyList<string>>> GetGlobalConfigOriginsAsync(CancellationToken cancellationToken = default)
        => _store.GetGlobalConfigOriginsAsync(cancellationToken);

    public async Task<GitRewritePlan> BuildApplyMissingPlanAsync(CancellationToken cancellationToken = default)
    {
        var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var expected = BuildCurrentExpectedRules(config);
        var actual = await _store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var plan = new GitRewritePlan();
        foreach (var rule in expected)
        {
            var samePrefix = actual.Any(item => string.Equals(item.InsteadOfUrl, rule.InsteadOfUrl, StringComparison.OrdinalIgnoreCase));
            if (!samePrefix)
            {
                plan.Adds.Add(rule);
            }
        }

        return plan;
    }

    public async Task<GitRewritePlan> BuildCleanupDuplicatesPlanAsync(CancellationToken cancellationToken = default)
    {
        var actual = await _store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var plan = new GitRewritePlan();
        foreach (var group in actual.GroupBy(rule => $"{rule.BaseUrl}\n{rule.InsteadOfUrl}", StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            var rule = group.First();
            plan.Removes.Add(rule);
            plan.Adds.Add(rule);
        }

        return plan;
    }

    public async Task<GitRewritePlan> BuildDeleteRulePlanAsync(
        string baseUrl,
        string insteadOfUrl,
        CancellationToken cancellationToken = default)
    {
        var actual = await _store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var plan = new GitRewritePlan();
        var rule = actual.FirstOrDefault(item => string.Equals(item.BaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.InsteadOfUrl, insteadOfUrl, StringComparison.OrdinalIgnoreCase));
        if (rule is not null)
        {
            plan.Removes.Add(rule);
        }

        return plan;
    }

    public async Task<GitRewritePlan> BuildDeleteOwnerPlanAsync(string owner, CancellationToken cancellationToken = default)
        => await BuildDeleteRoutePlanAsync(
            GitServiceInstance.GitHubComId,
            owner,
            cancellationToken).ConfigureAwait(false);

    public async Task<GitRewritePlan> BuildDeleteRoutePlanAsync(
        string serviceInstanceId,
        string namespacePath,
        CancellationToken cancellationToken = default)
    {
        var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var expected = BuildExpectedEntries(config)
            .Where(item => string.Equals(item.Route.ServiceInstanceId, serviceInstanceId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Route.NamespacePath, namespacePath, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Rule)
            .ToList();
        var actual = await _store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var plan = new GitRewritePlan();
        foreach (var rule in expected)
        {
            if (actual.Any(item => RuleEquals(item, rule))
                && !plan.Removes.Any(item => RuleEquals(item, rule)))
            {
                plan.Removes.Add(rule);
            }
        }

        return plan;
    }

    public async Task<GitRewritePlan> BuildDeleteRouteByIdPlanAsync(
        string routeId,
        CancellationToken cancellationToken = default)
    {
        var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var expected = BuildExpectedEntries(config)
            .Where(item => string.Equals(item.Route.Id, routeId, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Rule)
            .ToList();
        var actual = await _store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var plan = new GitRewritePlan();
        foreach (var rule in expected)
        {
            if (actual.Any(item => RuleEquals(item, rule))
                && !plan.Removes.Any(item => RuleEquals(item, rule)))
            {
                plan.Removes.Add(rule);
            }
        }

        return plan;
    }

    public async Task<GitRewritePlan> BuildRegeneratePlanAsync(CancellationToken cancellationToken = default)
    {
        var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var expected = BuildCurrentExpectedRules(config);
        var actual = await _store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var plan = new GitRewritePlan();
        foreach (var expectedRule in expected)
        {
            foreach (var currentRule in actual.Where(item => string.Equals(item.InsteadOfUrl, expectedRule.InsteadOfUrl, StringComparison.OrdinalIgnoreCase)))
            {
                if (!plan.Removes.Any(item => RuleEquals(item, currentRule)))
                {
                    plan.Removes.Add(currentRule);
                }
            }

            plan.Adds.Add(expectedRule);
        }

        return plan;
    }

    public async Task<GitRewritePlan> BuildReconcilePlanAsync(CancellationToken cancellationToken = default)
    {
        var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var expected = BuildCurrentExpectedRules(config);
        var actual = await _store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var plan = BuildReconcilePlan(expected, actual);
        var legacy = await BuildLegacyAccountOwnerMigrationPlanAsync(cancellationToken).ConfigureAwait(false);
        foreach (var rule in legacy.Removes)
        {
            AddUnique(plan.Removes, rule);
        }

        foreach (var rule in legacy.Adds)
        {
            AddUnique(plan.Adds, rule);
        }

        foreach (var routeId in legacy.RepositoryRouteIdsToRemove)
        {
            if (!plan.RepositoryRouteIdsToRemove.Contains(routeId, StringComparer.OrdinalIgnoreCase))
            {
                plan.RepositoryRouteIdsToRemove.Add(routeId);
            }
        }

        return plan;
    }

    public async Task<GitRewritePlan> BuildServiceRepairPlanAsync(
        string serviceInstanceId,
        CancellationToken cancellationToken = default)
    {
        var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        config.Normalize();
        var service = config.FindService(serviceInstanceId)
            ?? throw new InvalidOperationException($"Git service '{serviceInstanceId}' was not found.");
        var prefixes = _providers.Get(service.ProviderKind).GetSupportedRemotePatterns(service)
            .Select(item => item.Prefix)
            .ToList();
        var expected = BuildExpectedEntries(config)
            .Where(item => string.Equals(item.Service.Id, service.Id, StringComparison.OrdinalIgnoreCase)
                && !IsLegacyAccountOwnerEntry(item))
            .Select(item => item.Rule)
            .ToList();
        var actual = await _store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var plan = BuildReconcilePlan(expected, actual);
        var legacy = await BuildLegacyAccountOwnerMigrationPlanAsync(cancellationToken).ConfigureAwait(false);

        foreach (var rule in legacy.Removes.Where(rule => prefixes.Any(prefix =>
                     rule.InsteadOfUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))))
        {
            AddUnique(plan.Removes, rule);
        }

        foreach (var rule in legacy.Adds.Where(rule => prefixes.Any(prefix =>
                     rule.InsteadOfUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))))
        {
            AddUnique(plan.Adds, rule);
        }

        foreach (var routeId in legacy.RepositoryRouteIdsToRemove.Where(routeId => config.RepositoryRoutes.Any(route =>
                     string.Equals(route.Id, routeId, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(route.ServiceInstanceId, service.Id, StringComparison.OrdinalIgnoreCase))))
        {
            plan.RepositoryRouteIdsToRemove.Add(routeId);
        }

        return plan;
    }

    public async Task<GitRewritePlan> BuildLegacyAccountOwnerMigrationPlanAsync(CancellationToken cancellationToken = default)
    {
        var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var actual = await _store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        config.Normalize();
        var plan = new GitRewritePlan();
        foreach (var service in config.GitServices.Where(item => item.ProviderKind == GitProviderKind.Gitea
                     && !string.IsNullOrWhiteSpace(item.DefaultIdentityId)))
        {
            var identity = config.Identities.FirstOrDefault(item =>
                string.Equals(item.Id, service.DefaultIdentityId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ServiceInstanceId, service.Id, StringComparison.OrdinalIgnoreCase));
            if (identity is null || string.IsNullOrWhiteSpace(identity.AccountName))
            {
                continue;
            }

            var legacyConfigRoutes = config.RepositoryRoutes.Where(item => item.Enabled
                    && item.Scope == GitRouteScope.Owner
                    && string.Equals(item.ServiceInstanceId, service.Id, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.IdentityId, identity.Id, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.Owner ?? item.NamespacePath, identity.AccountName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var route in legacyConfigRoutes)
            {
                plan.RepositoryRouteIdsToRemove.Add(route.Id);
            }

            var legacyRoute = new RepositoryRoute
            {
                ServiceInstanceId = service.Id,
                IdentityId = identity.Id,
                Scope = GitRouteScope.Owner,
                Owner = identity.AccountName
            };
            var detected = _providers.Get(service.ProviderKind).BuildRewriteRules(service, identity, legacyRoute)
                .Where(rule => actual.Any(item => RuleEquals(item, rule)))
                .ToList();
            if (detected.Count == 0 && legacyConfigRoutes.Count == 0)
            {
                continue;
            }

            foreach (var rule in detected)
            {
                if (!plan.Removes.Any(item => RuleEquals(item, rule)))
                {
                    plan.Removes.Add(rule);
                }
            }

            var serviceRoute = new RepositoryRoute
            {
                ServiceInstanceId = service.Id,
                IdentityId = identity.Id,
                Scope = GitRouteScope.Service
            };
            foreach (var rule in _providers.Get(service.ProviderKind).BuildRewriteRules(service, identity, serviceRoute))
            {
                if (!actual.Any(item => RuleEquals(item, rule)) && !plan.Adds.Any(item => RuleEquals(item, rule)))
                {
                    plan.Adds.Add(rule);
                }
            }
        }

        return plan;
    }

    public async Task<OperationResult<IReadOnlyList<ProcessResult>>> ApplyPlanAsync(
        GitRewritePlan plan,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (!plan.HasChanges)
        {
            return OperationResult<IReadOnlyList<ProcessResult>>.Ok([], "Git URL rewrite rules already match.");
        }

        var reconciliations = new List<RewriteReconciliation>();
        try
        {
            var keys = plan.Adds.Concat(plan.Removes)
                .Select(item => item.ConfigKey)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
            {
                var current = await _store.GetValuesAsync(key, cancellationToken).ConfigureAwait(false);
                var removed = plan.Removes
                    .Where(item => string.Equals(item.ConfigKey, key, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.InsteadOfUrl)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var desired = current.Where(value => !removed.Contains(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var value in plan.Adds
                             .Where(item => string.Equals(item.ConfigKey, key, StringComparison.OrdinalIgnoreCase))
                             .Select(item => item.InsteadOfUrl))
                {
                    if (!desired.Contains(value, StringComparer.OrdinalIgnoreCase))
                    {
                        desired.Add(value);
                    }
                }

                if (!current.SequenceEqual(desired, StringComparer.OrdinalIgnoreCase))
                {
                    reconciliations.Add(new RewriteReconciliation(
                        key,
                        key[4..^".insteadOf".Length],
                        current.ToList(),
                        desired));
                }
            }
        }
        catch (Exception exception)
        {
            return OperationResult<IReadOnlyList<ProcessResult>>.Fail(
                "Failed to capture the affected Git URL rewrite keys. No changes were made.",
                exception.Message);
        }

        if (reconciliations.Count == 0 && plan.RepositoryRouteIdsToRemove.Count == 0)
        {
            return OperationResult<IReadOnlyList<ProcessResult>>.Ok([], "Git URL rewrite rules already match.");
        }

        AppConfig? originalConfig = null;
        AppConfig? updatedConfig = null;
        if (plan.RepositoryRouteIdsToRemove.Count > 0)
        {
            try
            {
                originalConfig = CloneConfig(
                    await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false));
                updatedConfig = CloneConfig(originalConfig);
                updatedConfig.RepositoryRoutes.RemoveAll(route =>
                    plan.RepositoryRouteIdsToRemove.Contains(route.Id, StringComparer.OrdinalIgnoreCase));
                updatedConfig.Normalize();
            }
            catch (Exception exception)
            {
                return OperationResult<IReadOnlyList<ProcessResult>>.Fail(
                    "Failed to prepare the application configuration for Git URL rewrite reconciliation.",
                    exception.Message);
            }
        }

        try
        {
            await _backupService.CreateSnapshotAsync(reason, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return OperationResult<IReadOnlyList<ProcessResult>>.Fail(
                "Failed to create a safety backup. No Git URL rewrite keys were changed.",
                exception.Message);
        }

        var results = new List<ProcessResult>();
        var gitMutationStarted = false;
        var configMutationAttempted = false;
        try
        {
            foreach (var reconciliation in reconciliations)
            {
                gitMutationStarted = true;
                var unset = await _store.RemoveAllForKeyAsync(
                    reconciliation.Key,
                    cancellationToken).ConfigureAwait(false);
                results.Add(unset);
                if (!unset.Succeeded && unset.ExitCode is not (1 or 5))
                {
                    throw new InvalidOperationException(
                        $"Failed to reset '{reconciliation.Key}': {DescribeProcessFailure(unset)}");
                }

                foreach (var value in reconciliation.Desired)
                {
                    var add = await _store.AddAsync(
                        new GitUrlRewriteRule(reconciliation.BaseUrl, value),
                        cancellationToken).ConfigureAwait(false);
                    results.Add(add);
                    if (!add.Succeeded)
                    {
                        throw new InvalidOperationException(
                            $"Failed to add '{value}' for '{reconciliation.Key}': {DescribeProcessFailure(add)}");
                    }
                }
            }

            var verificationErrors = await VerifyReconciliationsAsync(
                reconciliations,
                item => item.Desired,
                "Apply",
                cancellationToken).ConfigureAwait(false);
            if (verificationErrors.Count > 0)
            {
                throw new InvalidOperationException(string.Join(" ", verificationErrors));
            }

            if (updatedConfig is not null)
            {
                configMutationAttempted = true;
                await _configStore.SaveAsync(updatedConfig, cancellationToken).ConfigureAwait(false);
                var verifiedConfig = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                if (verifiedConfig.RepositoryRoutes.Any(route =>
                        plan.RepositoryRouteIdsToRemove.Contains(route.Id, StringComparer.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(
                        "Application configuration verification failed after removing migrated repository routes.");
                }
            }

            return OperationResult<IReadOnlyList<ProcessResult>>.Ok(
                results,
                "Git URL rewrite rules were reconciled and verified transactionally.");
        }
        catch (Exception exception)
        {
            var rollbackErrors = new List<string>();
            if (gitMutationStarted)
            {
                rollbackErrors.AddRange(await RestoreReconciliationsAsync(reconciliations).ConfigureAwait(false));
            }

            if (configMutationAttempted && originalConfig is not null)
            {
                try
                {
                    await _configStore.SaveAsync(originalConfig, CancellationToken.None).ConfigureAwait(false);
                    var restoredConfig = await _configStore.LoadAsync(CancellationToken.None).ConfigureAwait(false);
                    if (!ConfigsEqual(restoredConfig, originalConfig))
                    {
                        rollbackErrors.Add("Application configuration rollback verification failed.");
                    }
                }
                catch (Exception rollbackException)
                {
                    rollbackErrors.Add($"Application configuration rollback failed: {rollbackException.Message}");
                }
            }

            var applyError = $"Apply: {exception.Message}";
            if (rollbackErrors.Count == 0)
            {
                return OperationResult<IReadOnlyList<ProcessResult>>.Fail(
                    "Git URL rewrite reconciliation failed. The original affected keys and configuration were restored automatically.",
                    applyError);
            }

            return OperationResult<IReadOnlyList<ProcessResult>>.Fail(
                "Git URL rewrite reconciliation failed, and the automatic rollback also failed.",
                [applyError, .. rollbackErrors.Select(error => $"Rollback: {error}")]);
        }
    }

    private async Task<List<string>> RestoreReconciliationsAsync(
        IReadOnlyList<RewriteReconciliation> reconciliations)
    {
        var errors = new List<string>();
        foreach (var reconciliation in reconciliations)
        {
            try
            {
                var unset = await _store.RemoveAllForKeyAsync(
                    reconciliation.Key,
                    CancellationToken.None).ConfigureAwait(false);
                if (!unset.Succeeded && unset.ExitCode is not (1 or 5))
                {
                    errors.Add($"Failed to reset '{reconciliation.Key}': {DescribeProcessFailure(unset)}");
                    continue;
                }

                foreach (var value in reconciliation.Original)
                {
                    var add = await _store.AddAsync(
                        new GitUrlRewriteRule(reconciliation.BaseUrl, value),
                        CancellationToken.None).ConfigureAwait(false);
                    if (!add.Succeeded)
                    {
                        errors.Add(
                            $"Failed to restore '{value}' for '{reconciliation.Key}': {DescribeProcessFailure(add)}");
                    }
                }
            }
            catch (Exception exception)
            {
                errors.Add($"Failed to restore '{reconciliation.Key}': {exception.Message}");
            }
        }

        errors.AddRange(await VerifyReconciliationsAsync(
            reconciliations,
            item => item.Original,
            "Rollback",
            CancellationToken.None).ConfigureAwait(false));
        return errors;
    }

    private async Task<List<string>> VerifyReconciliationsAsync(
        IReadOnlyList<RewriteReconciliation> reconciliations,
        Func<RewriteReconciliation, IReadOnlyList<string>> expectedSelector,
        string phase,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        foreach (var reconciliation in reconciliations)
        {
            try
            {
                var actual = await _store.GetValuesAsync(
                    reconciliation.Key,
                    cancellationToken).ConfigureAwait(false);
                var expected = expectedSelector(reconciliation);
                if (!actual.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add($"{phase} verification failed for '{reconciliation.Key}'.");
                }
            }
            catch (Exception exception)
            {
                errors.Add($"{phase} verification failed for '{reconciliation.Key}': {exception.Message}");
            }
        }

        return errors;
    }

    private static AppConfig CloneConfig(AppConfig config)
        => JsonSerializer.Deserialize<AppConfig>(
            JsonSerializer.Serialize(config, ConfigCloneOptions),
            ConfigCloneOptions)
            ?? throw new InvalidOperationException("Failed to clone the application configuration.");

    private static bool ConfigsEqual(AppConfig left, AppConfig right)
        => string.Equals(
            JsonSerializer.Serialize(left, ConfigCloneOptions),
            JsonSerializer.Serialize(right, ConfigCloneOptions),
            StringComparison.Ordinal);

    private static string DescribeProcessFailure(ProcessResult result)
        => !string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardError.Trim()
            : result.StartException?.Message
                ?? (result.TimedOut
                    ? "The Git command timed out."
                    : result.Cancelled
                        ? "The Git command was cancelled."
                        : $"Git exited with code {result.ExitCode?.ToString() ?? "unknown"}.");

    private sealed record RewriteReconciliation(
        string Key,
        string BaseUrl,
        IReadOnlyList<string> Original,
        IReadOnlyList<string> Desired);

    public async Task<UrlRewritePreview> PreviewAsync(string originalUrl, CancellationToken cancellationToken = default)
    {
        var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var expected = BuildCurrentExpectedRules(config);
        var actual = await _store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var actualMatch = FindLongestMatch(originalUrl, actual);
        var expectedMatch = FindLongestMatch(originalUrl, expected);
        var displayMatch = expectedMatch ?? actualMatch;
        var expectedApplied = expectedMatch is not null && actual.Any(item => RuleEquals(item, expectedMatch));
        var expectedPrefixTargets = expectedMatch is null
            ? new List<GitUrlRewriteRule>()
            : actual.Where(item => string.Equals(item.InsteadOfUrl, expectedMatch.InsteadOfUrl, StringComparison.OrdinalIgnoreCase)).ToList();
        var expectationStatus = expectedMatch is null
            ? UrlRewriteExpectationStatus.None
            : expectedApplied
                ? UrlRewriteExpectationStatus.Applied
                : expectedPrefixTargets.Count > 0
                    ? UrlRewriteExpectationStatus.Conflict
                    : UrlRewriteExpectationStatus.Missing;

        return new UrlRewritePreview
        {
            OriginalUrl = originalUrl,
            MatchedPrefix = displayMatch?.InsteadOfUrl,
            MatchedBaseUrl = displayMatch?.BaseUrl,
            RewrittenUrl = Rewrite(originalUrl, displayMatch),
            ActualMatchedPrefix = actualMatch?.InsteadOfUrl,
            ActualMatchedBaseUrl = actualMatch?.BaseUrl,
            ActualRewrittenUrl = actualMatch is null ? null : Rewrite(originalUrl, actualMatch),
            ExpectedMatchedPrefix = expectedMatch?.InsteadOfUrl,
            ExpectedMatchedBaseUrl = expectedMatch?.BaseUrl,
            ExpectedRewrittenUrl = expectedMatch is null ? null : Rewrite(originalUrl, expectedMatch),
            ExpectationStatus = expectationStatus
        };
    }

    public Task<ProcessResult> TestRemoteAsync(string originalUrl, CancellationToken cancellationToken = default)
        => _store.TestRemoteAsync(originalUrl, cancellationToken);

    public async Task<GitRemoteConnectionResult> TestRemoteRouteAsync(
        string originalUrl,
        CancellationToken cancellationToken = default)
    {
        var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var match = _remoteUrlParser.Parse(originalUrl, config.GitServices);
        var service = match is null ? null : config.FindService(match.ServiceInstanceId);
        var process = await _store.TestRemoteAsync(originalUrl, cancellationToken).ConfigureAwait(false);
        var output = process.StandardOutput + Environment.NewLine + process.StandardError;
        var passwordFallback = output.Contains("password:", StringComparison.OrdinalIgnoreCase)
            || output.Contains("password for", StringComparison.OrdinalIgnoreCase)
            || output.Contains("keyboard-interactive", StringComparison.OrdinalIgnoreCase);
        var providerAuthenticated = service is not null
            && _providers.Get(service.ProviderKind).IsAuthenticationSuccess(process);
        var traceAuthenticated = output.Contains("Authenticated to ", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Authentication succeeded", StringComparison.OrdinalIgnoreCase);
        var authenticated = !passwordFallback && (process.Succeeded || providerAuthenticated || traceAuthenticated);
        var classification = passwordFallback
            ? "SSH authentication fell back to a password prompt."
            : authenticated && process.Succeeded
                ? "Git remote access and SSH key authentication succeeded."
                : authenticated
                    ? "SSH key authentication succeeded; the Git command returned a non-zero exit code."
                    : process.TimedOut
                        ? "Git remote access timed out."
                        : "Git remote access did not confirm SSH key authentication.";

        return new GitRemoteConnectionResult
        {
            Service = service,
            Process = process,
            AuthenticationSucceeded = authenticated,
            PasswordFallbackDetected = passwordFallback,
            Classification = classification
        };
    }

    public async Task<GitRemoteUrlMatch?> ParseRemoteUrlAsync(
        string remoteUrl,
        CancellationToken cancellationToken = default)
    {
        var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        return _remoteUrlParser.Parse(remoteUrl, config.GitServices);
    }

    private IReadOnlyList<ExpectedRouteRule> BuildExpectedEntries(AppConfig config)
    {
        config.Normalize();
        var result = new List<ExpectedRouteRule>();
        foreach (var route in config.RepositoryRoutes.Where(item => item.Enabled))
        {
            var service = config.FindService(route.ServiceInstanceId);
            var identity = config.Identities.FirstOrDefault(item =>
                string.Equals(item.Id, route.IdentityId, StringComparison.OrdinalIgnoreCase));
            if (service is null || identity is null
                || !string.Equals(identity.ServiceInstanceId, service.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var rule in _providers.Get(service.ProviderKind).BuildRewriteRules(service, identity, route))
            {
                result.Add(new ExpectedRouteRule(route, service, identity, rule));
            }
        }

        return result;
    }

    private IReadOnlyList<GitUrlRewriteRule> BuildCurrentExpectedRules(AppConfig config)
        => BuildExpectedEntries(config)
            .Where(item => !IsLegacyAccountOwnerEntry(item))
            .Select(item => item.Rule)
            .ToList();

    private static GitRewritePlan BuildReconcilePlan(
        IReadOnlyList<GitUrlRewriteRule> expected,
        IReadOnlyList<GitUrlRewriteRule> actual)
    {
        var plan = new GitRewritePlan();
        foreach (var expectedRule in expected)
        {
            var samePrefix = actual.Where(item =>
                    string.Equals(item.InsteadOfUrl, expectedRule.InsteadOfUrl, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var exactCount = samePrefix.Count(item => RuleEquals(item, expectedRule));
            if (exactCount == 1 && samePrefix.Count == 1)
            {
                continue;
            }

            foreach (var rule in samePrefix)
            {
                AddUnique(plan.Removes, rule);
            }

            AddUnique(plan.Adds, expectedRule);
        }

        return plan;
    }

    private static void AddUnique(List<GitUrlRewriteRule> rules, GitUrlRewriteRule rule)
    {
        if (!rules.Any(item => RuleEquals(item, rule)))
        {
            rules.Add(rule);
        }
    }

    private static bool RuleEquals(GitUrlRewriteRule left, GitUrlRewriteRule right)
        => string.Equals(left.BaseUrl, right.BaseUrl, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.InsteadOfUrl, right.InsteadOfUrl, StringComparison.OrdinalIgnoreCase);

    private static GitUrlRewriteRule? FindLongestMatch(string originalUrl, IEnumerable<GitUrlRewriteRule> rules)
        => rules.Where(rule => originalUrl.StartsWith(rule.InsteadOfUrl, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(rule => rule.InsteadOfUrl.Length)
            .FirstOrDefault();

    private static string Rewrite(string originalUrl, GitUrlRewriteRule? rule)
        => rule is null ? originalUrl : rule.BaseUrl + originalUrl[rule.InsteadOfUrl.Length..];

    private static bool IsLegacyAccountOwnerEntry(ExpectedRouteRule entry)
        => entry.Service.ProviderKind == GitProviderKind.Gitea
            && entry.Route.Scope == GitRouteScope.Owner
            && string.Equals(entry.Service.DefaultIdentityId, entry.Identity.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.Route.Owner ?? entry.Route.NamespacePath, entry.Identity.AccountName, StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<GitUrlRewriteRule> FindLegacyAccountOwnerRules(
        AppConfig config,
        IReadOnlyList<GitUrlRewriteRule> actual)
    {
        var result = new List<GitUrlRewriteRule>();
        foreach (var service in config.GitServices.Where(item => item.ProviderKind == GitProviderKind.Gitea
                     && !string.IsNullOrWhiteSpace(item.DefaultIdentityId)))
        {
            var identity = config.Identities.FirstOrDefault(item =>
                string.Equals(item.Id, service.DefaultIdentityId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ServiceInstanceId, service.Id, StringComparison.OrdinalIgnoreCase));
            if (identity is null || string.IsNullOrWhiteSpace(identity.AccountName))
            {
                continue;
            }

            var legacyRoute = new RepositoryRoute
            {
                ServiceInstanceId = service.Id,
                IdentityId = identity.Id,
                Scope = GitRouteScope.Owner,
                Owner = identity.AccountName
            };
            foreach (var expectedLegacy in _providers.Get(service.ProviderKind).BuildRewriteRules(service, identity, legacyRoute))
            {
                result.AddRange(actual.Where(item => RuleEquals(item, expectedLegacy)));
            }
        }

        return result;
    }

    private sealed record ExpectedRouteRule(
        RepositoryRoute Route,
        GitServiceInstance Service,
        GitIdentity Identity,
        GitUrlRewriteRule Rule);
}
