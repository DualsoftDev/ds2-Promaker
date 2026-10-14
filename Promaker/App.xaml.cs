using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using log4net;
using log4net.Config;
using Promaker.Presentation;

namespace Promaker;

public partial class App : Application
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(App));
    internal static DateTimeOffset RunStartedAt { get; } = DateTimeOffset.Now;

    internal static string RunId { get; } =
        $"{RunStartedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-pid{Environment.ProcessId}";

    /// <summary>더블클릭 등으로 전달된 파일 경로 (첫 번째 인자).</summary>
    internal static string? StartupFilePath { get; set; }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uPeriod);

    /// <summary>
    /// Windows 기본 system timer 해상도(15.6ms)는 Control 모드처럼 ms 단위 외부 PLC 연동 시
    /// WaitHandle.WaitAny timeout / Task.Delay / Thread.Sleep 의 정밀도가 부족함.
    /// 1ms 로 강제해서 simulationLoop 의 wakeSignal 이 timeout 으로 깰 때도 ms 단위 보장.
    /// Linux 는 커널 timer 가 기본 1ms 라 별도 설정 불필요.
    /// </summary>
    private const uint TimerPeriodMs = 1u;
    private bool _timerPeriodSet;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 3rd-party 테마 (AvalonDock / MahApps IconPacks) 내부의 GeometryDrawing.Brush={Binding Fill}
        // 류는 DataContext 상속 불가 위치에서 raise 되어 무해한 BindingExpression 경고를 발생시킨다.
        // Critical 만 유지하여 실 오류는 보존, severity 2 (Warning) 잡음만 제거.
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level =
            System.Diagnostics.SourceLevels.Critical;

        for (int i = 0; i < e.Args.Length; i++)
        {
            var arg = e.Args[i];
            if (StartupFilePath == null && File.Exists(arg))
                StartupFilePath = arg;
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (TimeBeginPeriod(TimerPeriodMs) == 0)
                    _timerPeriodSet = true;
            }
            catch
            {
                // winmm 호출 실패해도 동작은 가능 (정밀도만 보장 안 됨)
            }
        }
        // PR-A1 (DX → AvalonDock) — AvalonDock 은 일반 NuGet 어셈블리라 AppDomain.AssemblyResolve hook 불필요.
        // 호출은 외부 API 호환을 위해 유지 (DockHost.RegisterAssemblyResolve 는 no-op 박제).
        Promaker.Dock.DockHost.RegisterAssemblyResolve();

        // process working dir 가 exe 폴더가 아닐 수 있어 (단축키 / dotnet run / 다른 cwd 에서 실행)
        // AppContext.BaseDirectory (exe 폴더) 기준 절대 경로로 명시 — log4net Configure silent skip 회피.
        GlobalContext.Properties["PromakerRunId"] = RunId;
        var configFile = new FileInfo(Path.Combine(AppContext.BaseDirectory, "log4net.config"));
        if (configFile.Exists)
            XmlConfigurator.Configure(configFile);
        else
            System.Diagnostics.Trace.TraceWarning($"log4net.config not found at {configFile.FullName}. Logging may be disabled.");

        Log.Info(
            $"PROMAKER_RUN_BEGIN runId={RunId} pid={Environment.ProcessId} " +
            $"startedAt={RunStartedAt.ToString("O", CultureInfo.InvariantCulture)} baseDir=\"{AppContext.BaseDirectory}\"");

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Log.Fatal("Unhandled AppDomain exception", ex);
            else
                Log.Fatal($"Unhandled AppDomain exception (non-Exception): {args.ExceptionObject}");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal("Unhandled dispatcher exception", args.Exception);
            MessageBox.Show(
                $"A fatal UI error occurred and the app will stop.\n\n{args.Exception.Message}",
                "Fatal Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = false;
        };

        // Windows 로그오프·재부팅에서는 창 Closing 이 오지 않는다 — 저장 안 한 편집이 있으면 종료를 멈추고 평소의 저장 확인으로
        // 닫는다(사용자가 저장/폐기를 고른 뒤 다시 재부팅하면 된다).
        SessionEnding += (_, args) =>
        {
            if (MainWindow?.DataContext is Promaker.ViewModels.MainViewModel { IsDirty: true })
            {
                Log.Info($"Session ending ({args.ReasonSessionEnding}) with unsaved changes — cancel and ask to save.");
                args.Cancel = true;
                Dispatcher.BeginInvoke(new Action(() => MainWindow?.Close()));
            }
        };

        // AAStoPLC.TagWizard 의 모든 family preset 자동 등록 (idempotent). 외부 DLL(ExternalDlls) 로드가 여기서
        // 처음 일어난다 — 로깅과 fatal 핸들러가 준비된 뒤에 호출해야 실패가 로그에 남는다
        // (Ds2.Core 어셈블리 버전 바인딩 실패로 흔적 없이 죽던 사고).
        try
        {
            AAStoPLC.TagWizard.Bootstrap.EnsureRegistered();
        }
        catch (Exception ex)
        {
            Log.Fatal("AAStoPLC.TagWizard bootstrap failed — 외부 DLL(ExternalDlls) 로드 실패", ex);
            MessageBox.Show(
                $"필수 구성요소(AAStoPLC)를 불러오지 못해 Promaker 를 시작할 수 없습니다.\n\n{ex.Message}",
                "Promaker 시작 실패",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            throw;
        }

        ThemeManager.ApplySavedTheme();

        // AvalonDock 테마 — Promaker 라이트/다크에 연동 (dark=Vs2013DarkTheme, light=Vs2013LightTheme).
        // 격리 helper 호출 (AvalonDock type 외부 노출 0건 유지). 초기 1회 + 테마 전환 시 갱신.
        Promaker.Dock.DockHost.SetTheme(ThemeManager.CurrentTheme == AppTheme.Dark);
        ThemeManager.ThemeChanged += t => Promaker.Dock.DockHost.SetTheme(t == AppTheme.Dark);

        // GUI Log tab 의 AppLogState (singleton + ICollectionView) 를 UI thread 에서 강제 prefetch.
        // worker thread 의 첫 log 호출이 lazy 생성을 trigger 하면 CollectionView 가 worker SynchronizationContext
        // 에 묶여 이후 binding 시 NotSupportedException. fatal handler 등록 이후 시점이므로 ctor 예외 시 진단 가능.
        _ = Promaker.ViewModels.Logging.AppLogState.Instance;

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_timerPeriodSet && OperatingSystem.IsWindows())
        {
            try { TimeEndPeriod(TimerPeriodMs); } catch { }
        }

        var uptimeMs = (DateTimeOffset.Now - RunStartedAt).TotalMilliseconds;
        Log.Info(
            $"PROMAKER_RUN_END runId={RunId} pid={Environment.ProcessId} " +
            $"exitCode={e.ApplicationExitCode} uptimeMs={uptimeMs:F0}");
        base.OnExit(e);
    }
}
