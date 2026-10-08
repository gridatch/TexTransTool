# Roslyn C# syntax check

This workflow checks the **C# syntax** of the WDT source with Roslyn. It does
**not** compile the Unity project or validate references to Unity, Modular
Avatar, NDMF, Avatar Optimizer, or the Matsukawa HCE API.

## What is checked

- Every `.cs` file under the package source directories.
- Language version: **C# 9.0**, as used by Unity 2022.3.
- Multiple preprocessor-symbol profiles, including Unity Editor with and
  without commonly used optional VRChat/NDMF integrations.
- Parse errors (including `CS1002: ; expected`) stop CI.
- The scanner verifies itself using an intentionally invalid C# snippet.

The scanner runs in .NET 8 with a pinned Roslyn NuGet dependency. There is
**no Unity Editor, Unity license, VPM dependency restore, or secret**.

## Limitations

- Syntax analysis cannot detect missing types, invalid API calls, incompatible
  `.asmdef` references, or runtime integration issues.
- Only preprocessor branches selected by the configured profiles are parsed;
  not every mathematically possible combination is guaranteed.
- The Roslyn version is pinned for reproducibility; C# **language version 9**
  determines which C# grammar is accepted.

## Running locally

```sh
dotnet run --project .ci/syntax-check/CSharpSyntaxCheck.csproj --configuration Release -- .
```

See `.github/workflows/CSharpSyntax.yml` for the GitHub Actions job.

## Distribution

Debug and Release packagers explicitly exclude `.ci`. The syntax workflow
does not publish or modify the package. In this PR the existing Debug and
Release workflows call the same scanner as a prerequisite; publishing does not
run if syntax validation fails. Changes are not active until the PR is merged.
GitHub Actions runs confirmed 350 source files, 15 profiles, zero errors, and
an expected rejection of a missing-semicolon file.
