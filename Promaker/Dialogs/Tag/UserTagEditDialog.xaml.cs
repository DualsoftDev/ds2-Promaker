using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Ds2.Editor;

namespace Promaker.Dialogs;

public partial class UserTagEditDialog : Window
{
    private static readonly string[] PlcValueTypes =
        ["Bit", "Byte", "Word", "DWord", "Int16", "Int32", "Real", "String"];

    // (display, op id) — op id 는 F# UserTagHelpers.parseMatchOp 가 받는 키.
    private static readonly (string Display, string Op)[] BitMatchOps =
    [
        ("Rising Edge (0 → 1)",  "RisingEdge"),
        ("Falling Edge (1 → 0)", "FallingEdge"),
        ("값과 같아질 때 (=)",     "Eq"),
        ("값과 달라질 때 (≠)",     "Neq"),
        ("변할 때마다",            "Changed"),
    ];

    private static readonly (string Display, string Op)[] NumericMatchOps =
    [
        ("같아질 때 (=)",   "Eq"),
        ("달라질 때 (≠)",   "Neq"),
        ("초과 (>)",        "Gt"),
        ("이상 (≥)",        "Gte"),
        ("미만 (<)",        "Lt"),
        ("이하 (≤)",        "Lte"),
        ("변할 때마다",     "Changed"),
    ];

    private static readonly (string Display, string Op)[] StringMatchOps =
    [
        ("같아질 때 (=)", "Eq"),
        ("달라질 때 (≠)", "Neq"),
        ("변할 때마다",   "Changed"),
    ];

    private readonly HashSet<string> _existingNamesLower;
    private readonly UserTagPanelItem? _existing;
    private bool _initializing = true;

    public string TagName { get; private set; } = string.Empty;
    // 사용자 태그는 모두 Error 로 취급(운영 정책). Warning/Info 는 타입엔 존재하나 편집기에서 설정 불가.
    public string LogLevel { get; private set; } = "Error";
    public string TagAddress { get; private set; } = string.Empty;
    public string ValueType { get; private set; } = "Bit";
    public string MatchOp { get; private set; } = "RisingEdge";
    public string MatchValue { get; private set; } = string.Empty;

    public UserTagEditDialog(IEnumerable<string> existingNames, UserTagPanelItem? existing = null)
    {
        InitializeComponent();

        _existing = existing;
        _existingNamesLower = new HashSet<string>(
            existingNames?.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim().ToLowerInvariant())
            ?? Enumerable.Empty<string>());

        foreach (var vt in PlcValueTypes)
            ValueTypeCombo.Items.Add(vt);

        if (existing is not null)
        {
            NameBox.Text = existing.Name;
            AddressBox.Text = existing.TagAddress;
            // 레벨은 Error 로 고정(레거시 Warning/Info 태그도 저장 시 Error 로 승격).

            ValueTypeCombo.SelectedItem = PlcValueTypes.Contains(existing.ValueType)
                ? existing.ValueType
                : "Bit";

            RebuildMatchOpItems(SelectedValueType());
            SelectMatchOp(existing.MatchOp);
            MatchValueBox.Text = existing.MatchValue ?? string.Empty;
        }
        else
        {
            ValueTypeCombo.SelectedItem = "Bit";
            RebuildMatchOpItems("Bit");
            SelectMatchOp("RisingEdge");
        }

        _initializing = false;
        UpdatePreview();

        Loaded += (_, _) => NameBox.Focus();
    }

    private string SelectedValueType() =>
        (ValueTypeCombo.SelectedItem as string) ?? "Bit";

    private void RebuildMatchOpItems(string valueType)
    {
        var prev = (MatchOpCombo.SelectedItem as MatchOpItem)?.Op;
        MatchOpCombo.Items.Clear();
        var ops = valueType switch
        {
            "Bit"    => BitMatchOps,
            "String" => StringMatchOps,
            _        => NumericMatchOps,
        };
        foreach (var (display, op) in ops)
            MatchOpCombo.Items.Add(new MatchOpItem(display, op));

        // 이전 선택 복구; 없으면 첫 항목.
        if (!string.IsNullOrEmpty(prev))
            SelectMatchOp(prev);
        if (MatchOpCombo.SelectedIndex < 0 && MatchOpCombo.Items.Count > 0)
            MatchOpCombo.SelectedIndex = 0;
    }

    private void SelectMatchOp(string op)
    {
        foreach (MatchOpItem item in MatchOpCombo.Items)
        {
            if (string.Equals(item.Op, op, StringComparison.OrdinalIgnoreCase))
            {
                MatchOpCombo.SelectedItem = item;
                return;
            }
        }
        // 못 찾으면 첫 번째 항목으로.
        if (MatchOpCombo.Items.Count > 0)
            MatchOpCombo.SelectedIndex = 0;
    }

    private string CurrentMatchOp() =>
        (MatchOpCombo.SelectedItem as MatchOpItem)?.Op ?? "RisingEdge";

    private void ValueTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        RebuildMatchOpItems(SelectedValueType());
        UpdatePreview();
    }

    private void MatchOpCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        UpdatePreview();
    }

    private void MatchValueBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_initializing) return;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? "(이름)" : NameBox.Text.Trim();
        var op = CurrentMatchOp();
        var mv = MatchValueBox.Text?.Trim() ?? string.Empty;
        const string levelText = "Error"; // 사용자 태그는 모두 Error 취급

        var condition = op switch
        {
            "RisingEdge"  => "값이 0 → 1 로 바뀔 때",
            "FallingEdge" => "값이 1 → 0 으로 바뀔 때",
            "Changed"     => "값이 바뀔 때마다",
            "Eq"          => $"값이 '{mv}' 와 같아질 때",
            "Neq"         => $"값이 '{mv}' 와 달라질 때",
            "Gt"          => $"값이 '{mv}' 보다 커질 때",
            "Gte"         => $"값이 '{mv}' 이상이 될 때",
            "Lt"          => $"값이 '{mv}' 보다 작아질 때",
            "Lte"         => $"값이 '{mv}' 이하가 될 때",
            _             => "?",
        };

        PreviewText.Text = $"'{name}': {condition} → {levelText} 알림 생성";

        var needsValue = op is "Eq" or "Neq" or "Gt" or "Gte" or "Lt" or "Lte";
        MatchValueBox.IsEnabled = needsValue;
        if (!needsValue) MatchValueBox.Opacity = 0.5;
        else MatchValueBox.Opacity = 1.0;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            DialogHelpers.Warn("이름을 입력해주세요.");
            return;
        }

        if (_existingNamesLower.Contains(name.ToLowerInvariant()))
        {
            DialogHelpers.Warn($"'{name}' 이름이 이미 존재합니다.");
            return;
        }

        var addr = AddressBox.Text.Trim();
        if (string.IsNullOrEmpty(addr))
        {
            DialogHelpers.Warn("태그 주소를 입력해주세요.");
            return;
        }

        var op = CurrentMatchOp();
        var mv = MatchValueBox.Text?.Trim() ?? string.Empty;

        // 값이 필요한 op 인데 비어있으면 차단.
        var needsValue = op is "Eq" or "Neq" or "Gt" or "Gte" or "Lt" or "Lte";
        if (needsValue && string.IsNullOrEmpty(mv))
        {
            DialogHelpers.Warn("매칭 조건의 기준값을 입력해주세요.");
            return;
        }

        (TagName, LogLevel, TagAddress, ValueType, MatchOp, MatchValue) =
            ResolveResult(_existing, name, addr, SelectedValueType(), op, mv, needsValue);

        DialogResult = true;
    }

    /// <summary>
    /// 확인 시 돌려줄 값. 보이는 칸(이름·주소·값 형식·조건, 조건이 쓰는 기준값)이 기존 태그와 같으면 기존 값을 그대로 돌려준다(#230).
    /// 새로 만들거나 칸을 바꾸면 정책대로 로그 레벨을 Error 로 올리고(Warning/Info 미사용), Edge·Changed 조건의 기준값은 비운다.
    /// 예전엔 아무것도 안 바꿔도 이 정규화로 값이 달라져(Info → Error 등) History 에 "사용자 태그 편집" 이 쌓였다.
    /// </summary>
    internal static (string Name, string LogLevel, string Address, string ValueType, string MatchOp, string MatchValue) ResolveResult(
        UserTagPanelItem? existing, string name, string address, string valueType, string matchOp, string matchValue, bool needsValue)
    {
        var unchanged = existing is not null
            && existing.Name == name
            && existing.TagAddress == address
            && existing.ValueType == valueType
            && string.Equals(existing.MatchOp, matchOp, StringComparison.OrdinalIgnoreCase)
            && (!needsValue || (existing.MatchValue ?? string.Empty) == matchValue);
        if (unchanged)
            return (existing!.Name, existing.LogLevel, existing.TagAddress, existing.ValueType, existing.MatchOp,
                    existing.MatchValue ?? string.Empty);

        return (name, "Error", address, valueType, matchOp, needsValue ? matchValue : string.Empty);
    }

    private sealed record MatchOpItem(string Display, string Op)
    {
        public override string ToString() => Display;
    }
}
