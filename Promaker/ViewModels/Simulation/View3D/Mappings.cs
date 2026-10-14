using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Ds2.View3D;

namespace Promaker.ViewModels;

public partial class ThreeDViewState
{
    private static SceneEngine CreateSceneEngine(DsStore store)
    {
        var layoutDir = GetLayoutDirectory();
        return new SceneEngine(store, new Ds2.View3D.Persistence.JsonFileLayoutStore(layoutDir));
    }

    private void ClearCaches()
    {
        _callToDevice.Clear();
        _callToApiDef.Clear();
        _deviceToSystem.Clear();
        _apiDefStateCache.Clear();
        _deviceToApiDefs.Clear();
    }

    private void BuildMappings(DsStore store, SceneData scene)
    {
        // 장치 노드의 Id 는 그 장치 System 의 Id 다(Ds2.View3D ContextBuilder). 이름으로 매핑하면 대소문자만 다른 장치
        // 이름(Cyl1 / CYL1)에서 ToDictionary 가 예외를 던져 3D 씬이 영구히 비었다.
        foreach (var device in scene.Devices)
        {
            if (store.Systems.ContainsKey(device.Id))
                _deviceToSystem[device.Id] = device.Id;
        }

        // Store all ApiDefs for each Device (from SceneData)
        foreach (var deviceNode in scene.Devices)
        {
            var apiDefList = deviceNode.ApiDefs.ToList();
            if (apiDefList.Count > 0)
            {
                _deviceToApiDefs[deviceNode.Id] = new HashSet<Guid>(
                    apiDefList.Select(a => a.Id));
            }
        }

        BuildCallToDeviceMapping(store);
    }

    /// <summary>
    /// Call → ApiDef → TargetDevice 체인 추적으로 정확한 매핑 구축.
    /// ApiDef.ParentId = System (Device)이므로 ApiCall을 통해 어느 Device가 호출되는지 추적한다.
    /// </summary>
    private void BuildCallToDeviceMapping(DsStore store)
    {
        foreach (var (callId, call) in store.Calls)
        {
            foreach (var apiCall in call.ApiCalls)
            {
                if (apiCall.ApiDefId == null) continue;

                var apiDefId = apiCall.ApiDefId.Value;

                // ApiCall → ApiDef 추적
                if (!store.ApiDefs.TryGetValue(apiDefId, out var apiDef))
                    continue;

                // Call → ApiDef 매핑
                _callToApiDef[callId] = apiDefId;

                // ApiDef.ParentId = TargetSystem (Device)
                var targetSystemId = apiDef.ParentId;
                if (!store.Systems.TryGetValue(targetSystemId, out var targetSystem))
                    continue;

                // 장치 Id = 장치 System Id — 씬에 있는 장치면 그대로 매핑한다(이름 비교는 대소문자 충돌로 깨졌다).
                if (_deviceToSystem.ContainsKey(targetSystem.Id))
                {
                    _callToDevice[callId] = targetSystem.Id;
                    break;  // 하나의 Call은 하나의 주요 Device에만 매핑
                }
            }
        }
    }

}
