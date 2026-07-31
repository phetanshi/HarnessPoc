using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.MSBuild;
using Nuke.Common.Tools.NuGet;
using Nuke.Common.Tools.VSTest;
using Nuke.Common.Utilities.Collections;
using Serilog;
using static Nuke.Common.Tools.MSBuild.MSBuildTasks;
using static Nuke.Common.Tools.NuGet.NuGetTasks;
using static Nuke.Common.Tools.VSTest.VSTestTasks;

/// <summary>
/// Defines the repeatable build pipeline for the .NET Framework solution.
/// </summary>
class Build : NukeBuild
{
    /// <summary>
    /// Runs the complete build when no target is supplied to a NUKE bootstrap script.
    /// </summary>
    public static int Main() => Execute<Build>(build => build.Publish);

    /// <summary>
    /// Gets the build configuration. Local builds default to Debug and CI builds to Release.
    /// </summary>
    [Parameter("Configuration to build (Debug or Release)")]
    readonly Configuration Configuration = IsLocalBuild
        ? Configuration.Debug
        : Configuration.Release;

    /// <summary>
    /// Gets the solution to process. Override with <c>--solution path/to/file.sln</c>.
    /// </summary>
    [Solution(GenerateProjects = true)]
    readonly Solution Solution;

    /// <summary>
    /// Gets the root directory for packages, test results, and published output.
    /// </summary>
    [Parameter("Artifacts directory")]
    readonly AbsolutePath ArtifactsDirectory = RootDirectory / "artifacts";

    AbsolutePath TestResultsDirectory => ArtifactsDirectory / "test-results";
    AbsolutePath PackagesDirectory => ArtifactsDirectory / "packages";
    AbsolutePath PublishDirectory => ArtifactsDirectory / "publish";

    IEnumerable<Project> ProductProjects =>
        Solution.AllProjects.Where(project =>
            !project.Path.ToString().Equals(
                BuildProjectFile.ToString(),
                StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Removes project intermediates, binaries, and all previously generated artifacts.
    /// </summary>
    Target Clean => target => target
        .Description("Deletes project bin/obj directories and generated artifacts")
        .Executes(() =>
        {
            Log.Information("Cleaning project output and artifact directories...");

            ProductProjects
                .SelectMany(project => project.Directory.GlobDirectories("**/bin", "**/obj"))
                .Distinct()
                .ForEach(directory => directory.DeleteDirectory());

            ArtifactsDirectory.CreateOrCleanDirectory();
        });

    /// <summary>
    /// Restores packages.config and PackageReference dependencies for the solution.
    /// </summary>
    Target Restore => target => target
        .Description("Restores NuGet packages for the solution")
        .DependsOn(Clean)
        .Executes(() =>
        {
            Log.Information("Restoring NuGet packages for {Solution}...", Solution.Path);

            NuGetRestore(settings => settings
                .SetTargetPath(Solution.Path)
                .SetNonInteractive(true));
        });

    /// <summary>
    /// Builds the solution with full-framework MSBuild.
    /// </summary>
    Target Compile => target => target
        .Description("Builds the .NET Framework solution")
        .DependsOn(Restore)
        .Executes(() =>
        {
            Log.Information(
                "Building {Solution} in {Configuration} configuration...",
                Solution.Path,
                Configuration);

            MSBuild(settings => settings
                .SetProjectFile(Solution.Path)
                .SetConfiguration(Configuration)
                .SetTargets("Build")
                .SetMaxCpuCount(Environment.ProcessorCount)
                .SetNodeReuse(false)
                .SetProperty("RestorePackages", false)
                .SetProperty("TreatWarningsAsErrors", true));
        });

    /// <summary>
    /// Runs test assemblies from conventionally named test projects through VSTest.
    /// Test projects must reference the appropriate MSTest, NUnit, or xUnit VSTest adapter.
    /// </summary>
    Target Test => target => target
        .Description("Runs all unit tests with VSTest")
        .DependsOn(Compile)
        .Executes(() =>
        {
            var testAssemblies = ProductProjects
                .Where(project => project.Name.EndsWith("Test", StringComparison.OrdinalIgnoreCase) ||
                                  project.Name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase))
                .SelectMany(project => (project.Directory / "bin").GlobFiles($"**/{project.Name}.dll"))
                .Where(path => File.Exists(path))
                .ToList();

            if (testAssemblies.Count == 0)
            {
                Log.Information("No test projects or test assemblies were found; skipping test execution.");
                return;
            }

            TestResultsDirectory.CreateOrCleanDirectory();
            Log.Information("Running {AssemblyCount} test assembly/assemblies...", testAssemblies.Count);

            VSTest(settings => settings
                .SetTestAssemblies(testAssemblies.Select(path => path.ToString()).ToArray())
                .SetProcessWorkingDirectory(TestResultsDirectory)
                .SetLogger("trx")
                .EnableParallel());
        });

    /// <summary>
    /// Creates NuGet packages for any package specifications present in the repository.
    /// This target is intentionally not part of the default pipeline.
    /// </summary>
    Target Pack => target => target
        .Description("Creates NuGet packages from repository .nuspec files")
        .DependsOn(Test)
        .Executes(() =>
        {
            var packageSpecifications = RootDirectory.GlobFiles("**/*.nuspec");

            if (packageSpecifications.Count == 0)
            {
                Log.Information("No .nuspec files were found; skipping package creation.");
                return;
            }

            PackagesDirectory.CreateDirectory();
            Log.Information(
                "Packing {SpecificationCount} NuGet package specification(s)...",
                packageSpecifications.Count);

            NuGetPack(settings => packageSpecifications
                .Select(specification => settings
                    .SetTargetPath(specification)
                    .SetOutputDirectory(PackagesDirectory)
                    .SetProperties(new Dictionary<string, object>
                    {
                        ["Configuration"] = Configuration
                    })));
        });

    /// <summary>
    /// Copies each product project's compiled output into a deterministic artifact directory.
    /// </summary>
    Target Publish => target => target
        .Description("Publishes compiled project outputs to the artifacts directory")
        .DependsOn(Test)
        .Executes(() =>
        {
            PublishDirectory.CreateOrCleanDirectory();
            Log.Information("Publishing build outputs to {PublishDirectory}...", PublishDirectory);

            foreach (var project in ProductProjects)
            {
                var sourceDirectory = project.Directory / "bin";
                if (!Directory.Exists(sourceDirectory))
                {
                    Log.Warning("No output directory found for project {Project}; skipping it.", project.Name);
                    continue;
                }

                var destinationDirectory = PublishDirectory / project.Name;
                Log.Information(
                    "Copying {SourceDirectory} to {DestinationDirectory}...",
                    sourceDirectory,
                    destinationDirectory);
                sourceDirectory.Copy(destinationDirectory, ExistsPolicy.MergeAndOverwrite);
            }
        });
}
