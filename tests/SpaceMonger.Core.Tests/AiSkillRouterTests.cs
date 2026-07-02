using FluentAssertions;
using SpaceMonger.Core.Services.Copilot;

namespace SpaceMonger.Core.Tests;

public class AiSkillRouterTests
{
    private readonly AiSkillRouter _router = new(new FileSkillPromptProvider());

    [Fact]
    public void Route_NaturalLanguageUnityRequest_DoesNotInjectSkillPrompt()
    {
        var result = _router.Route("clean my Unity project", null, null, hasExistingRecommendations: false);

        result.SelectedSkillIds.Should().BeEmpty();
        result.Skills.Should().BeEmpty();
    }

    [Fact]
    public void Route_MixedChineseUnityRequest_DoesNotInjectSkillPrompt()
    {
        var result = _router.Route("整理我的 unity 项目", null, null, hasExistingRecommendations: false);

        result.SelectedSkillIds.Should().BeEmpty();
        result.Skills.Should().BeEmpty();
    }


    [Fact]
    public void Route_TempScanRequest_DoesNotMatchSkillPromptTokens()
    {
        var result = _router.Route("扫描 %TEMP%", null, null, hasExistingRecommendations: false);

        result.SelectedSkillIds.Should().BeEmpty();
        result.Skills.Should().BeEmpty();
    }

    [Fact]
    public void Route_NaturalLanguageRecommendationRequest_DoesNotInjectSkillPrompt()
    {
        var result = _router.Route("推荐清理 userprofile", null, null, hasExistingRecommendations: true);

        result.SelectedSkillIds.Should().BeEmpty();
        result.Skills.Should().BeEmpty();
    }

    [Fact]
    public void Route_SelectedPathCleanupSkill_InjectsSkillPromptOnly()
    {
        var result = _router.Route(@"@path-cleanup-recommendation 推荐清理 D:\Downloads", null, null, hasExistingRecommendations: true);

        result.SelectedSkillIds.Should().ContainSingle().Which.Should().Be("path-cleanup-recommendation");
        result.Skills.Select(skill => skill.Id).Should().ContainSingle().Which.Should().Be("path-cleanup-recommendation");
        result.Skills.Single().Prompt.Should().Contain("Path Cleanup Recommendation Skill");
    }

    [Fact]
    public void Route_GeneralChat_DoesNotInjectUnrelatedSkills()
    {
        var result = _router.Route("who are you?", null, null, hasExistingRecommendations: false);

        result.Skills.Should().BeEmpty();
        result.SelectedSkillIds.Should().BeEmpty();
    }

    [Fact]
    public void Route_SelectedUnitySkill_InjectsSkillPromptOnly()
    {
        var result = _router.Route("@unity-project-cleanup clean every Unity Library", null, null, hasExistingRecommendations: false);

        result.SelectedSkillIds.Should().ContainSingle().Which.Should().Be("unity-project-cleanup");
        result.Skills.Select(skill => skill.Id).Should().ContainSingle().Which.Should().Be("unity-project-cleanup");
        result.Skills.Single().Prompt.Should().Contain("Unity Project Cleanup Skill");
    }

    [Fact]
    public void Route_SelectedDiskManagementSkill_InjectsSkillWithoutParsingPathKeywords()
    {
        var result = _router.Route(@"@disk-management scan D:\Downloads", null, null, hasExistingRecommendations: true);

        result.SelectedSkillIds.Should().ContainSingle().Which.Should().Be("disk-management");
        result.Skills.Select(skill => skill.Id).Should().ContainSingle().Which.Should().Be("disk-management");
        result.Skills.Single().Prompt.Should().Contain("Disk Management Copilot Skill");
    }

    [Fact]
    public void Route_SelectedAppGuideSkill_InjectsSkillPromptOnly()
    {
        var result = _router.Route("@app-guide who are you?", null, null, hasExistingRecommendations: false);

        result.SelectedSkillIds.Should().ContainSingle().Which.Should().Be("app-guide");
        result.Skills.Select(skill => skill.Id).Should().ContainSingle().Which.Should().Be("app-guide");
        result.Skills.Single().Prompt.Should().Contain("SpaceMonger Copilot App Guide Skill");
    }

    [Fact]
    public void Route_UnknownSkillMention_IsIgnored()
    {
        var result = _router.Route("@does-not-exist clean something", null, null, hasExistingRecommendations: false);

        result.SelectedSkillIds.Should().BeEmpty();
        result.Skills.Should().BeEmpty();
    }

    [Fact]
    public void GetSkillCatalog_ReturnsMentionableSkillsFromSkillFiles()
    {
        var catalog = _router.GetSkillCatalog();

        catalog.Select(skill => skill.Id).Should().Contain(["app-guide", "disk-management", "path-cleanup-recommendation", "unity-project-cleanup"]);
        catalog.Should().OnlyContain(skill => !string.IsNullOrWhiteSpace(skill.Description));
        catalog.Single(skill => skill.Id == "app-guide").DisplayName.Should().Be("SpaceMonger Copilot App Guide Skill");
    }

    [Fact]
    public void Route_CustomSkillFile_IsDiscoveredWithoutRouterCodeChange()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "spacemonger-skill-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var skillDirectory = Path.Combine(tempRoot, "custom-disk-skill");
            Directory.CreateDirectory(skillDirectory);
            File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), """
                # Custom Disk Skill

                ## Purpose
                A test-only skill loaded from a folder, not from router source code.

                ## Instructions
                Use this custom skill prompt.
                """, System.Text.Encoding.UTF8);
            var router = new AiSkillRouter(new FileSkillPromptProvider([tempRoot]));

            var result = router.Route("@custom-disk-skill inspect cache", null, null, hasExistingRecommendations: false);

            router.GetSkillCatalog().Should().ContainSingle(skill =>
                skill.Id == "custom-disk-skill" &&
                skill.DisplayName == "Custom Disk Skill" &&
                skill.Description.Contains("loaded from a folder"));
            result.SelectedSkillIds.Should().ContainSingle().Which.Should().Be("custom-disk-skill");
            result.Skills.Should().ContainSingle(skill =>
                skill.Id == "custom-disk-skill" &&
                skill.Prompt.Contains("Use this custom skill prompt."));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void GetSkillSource_AppGuide_ComesFromSkillFile()
    {
        var content = _router.GetSkillSource("app-guide");

        content.Should().NotBeNull();
        content.Should().Contain("# SpaceMonger Copilot App Guide Skill");
        content.Should().Contain("## Module Guide");
    }
}


