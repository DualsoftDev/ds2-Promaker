using System;
using System.IO;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// 3D 커스텀 모델 레지스트리 회귀. JSON 편집창의 실시간 검증·등록·삭제가 예외로 앱을 종료시키거나(객체가 아닌 JSON,
/// 파일 이름 금지 문자, 잠긴 파일), 기존 모델 파일을 잃게 하던(이름 변경 시 먼저 삭제, 대소문자만 다른 이름 덮어쓰기) 경로.
/// </summary>
public sealed class CustomModelRegistryTests : IDisposable
{
    private const string ValidJson = "{ \"name\": \"X\", \"height\": 1, \"parts\": [] }";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"custom-models-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (!Directory.Exists(_dir)) return;
        foreach (var f in Directory.GetFiles(_dir)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_dir, true);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("1")]
    [InlineData("\"abc\"")]
    [InlineData("null")]
    public void Validate_rejects_json_whose_root_is_not_an_object_without_throwing(string json)
    {
        var (isValid, error) = CustomModelRegistry.Validate(json);

        Assert.False(isValid);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("Robot/ABB")]
    [InlineData("A|B")]
    [InlineData("Cyl?2")]
    public void A_name_with_file_name_characters_is_refused_before_touching_disk(string systemType)
    {
        var registry = new CustomModelRegistry(_dir);

        Assert.False(registry.TrySave(systemType, ValidJson, out var error));
        Assert.Contains("파일 이름", error);
        Assert.False(Directory.Exists(_dir) && Directory.GetFiles(_dir).Length > 0);
    }

    [Fact]
    public void Renaming_keeps_the_old_model_when_the_new_name_cannot_be_saved()
    {
        var registry = new CustomModelRegistry(_dir);
        Assert.True(registry.TrySave("Old", ValidJson, out _));

        Assert.False(registry.TryRename("Old", "Bad|Name", ValidJson, out _));

        Assert.True(File.Exists(Path.Combine(_dir, "Old.device.json")));
        Assert.True(registry.Models.ContainsKey("Old"));
    }

    [Fact]
    public void Model_names_are_case_insensitive_like_the_file_system()
    {
        var registry = new CustomModelRegistry(_dir);
        Assert.True(registry.TrySave("Robot_A", ValidJson, out _));

        Assert.True(registry.Models.ContainsKey("ROBOT_A"));
    }

    [Fact]
    public void Deleting_a_read_only_model_reports_failure_and_keeps_it_listed()
    {
        var registry = new CustomModelRegistry(_dir);
        Assert.True(registry.TrySave("Locked", ValidJson, out _));
        File.SetAttributes(Path.Combine(_dir, "Locked.device.json"), FileAttributes.ReadOnly);

        Assert.False(registry.TryDelete("Locked", out var error));

        Assert.False(string.IsNullOrEmpty(error));
        Assert.True(registry.Models.ContainsKey("Locked"));
    }
}
