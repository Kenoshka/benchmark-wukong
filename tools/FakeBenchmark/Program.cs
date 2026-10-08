// Имитатор меню Black Myth: Wukong Benchmark Tool для интеграционной проверки WukongAutoBench.
// Окно 1280x720 с теми же состояниями, что у бенчмарка: заставка (Enter) → меню (клик «Тест быстродействия»)
// → диалог (клик «Подтвердить» или Enter) → «тест» 8 с → JSON-результат в %TEMP%\b1\BenchMarkHistory\Tool.
// Клики принимаются только рядом с координатами кнопок, все события пишутся в fake.log рядом с exe.
using System.Globalization;
using System.Text;

var log = Path.Combine(AppContext.BaseDirectory, "fake.log");
void L(string s) => File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss.fff} {s}\n");
L("start " + string.Join(' ', args));
var state = "splash";
var form = new Form { Text = "b1 (fake)", ClientSize = new Size(1280, 720), StartPosition = FormStartPosition.CenterScreen, KeyPreview = true };
var label = new Label { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 28), TextAlign = ContentAlignment.MiddleCenter, Text = "Нажмите любую кнопку" };
form.Controls.Add(label);
void Set(string s, string text) { state = s; label.Text = text; L("state=" + s); }
form.KeyDown += (_, e) =>
{
    L($"key {e.KeyCode} in {state}");
    if (e.KeyCode == Keys.Enter && state == "splash") Set("menu", "Главное меню");
    else if (e.KeyCode == Keys.Enter && state == "dialog") Start();
};
label.MouseClick += (_, e) =>
{
    var c = form.ClientSize;
    double fx = e.X / (double)c.Width, fy = e.Y / (double)c.Height;
    L($"click {fx:0.000},{fy:0.000} in {state}");
    if (state == "menu" && Math.Abs(fx - 0.11) < 0.06 && Math.Abs(fy - 0.455) < 0.03) Set("dialog", "Подтвердить?");
    else if (state == "dialog" && Math.Abs(fx - 0.39) < 0.06 && Math.Abs(fy - 0.59) < 0.03) Start();
};
void Start()
{
    Set("running", "Идёт тест…");
    var t = new System.Windows.Forms.Timer { Interval = 8000 };
    t.Tick += (_, _) =>
    {
        t.Stop();
        var dir = Path.Combine(Path.GetTempPath(), "b1", "BenchMarkHistory", "Tool");
        Directory.CreateDirectory(dir);
        var rnd = new Random(1);
        var sb = new StringBuilder("{\"FPSAvg\":111.1,\"FPSMax\":150.0,\"FPSMin\":70.0,\"FPS95\":95.0,\"CPUAvg\":80,\"GPUAvg\":40,\"VideoMem\":4.9,");
        sb.Append("\"GameVer\":\"fake\",\"CPUModel\":\"fake cpu\",\"GPUModel\":\"fake gpu\",\"ScreenMode\":0,\"ScreenResolution\":\"1280 \u00d7 720\",");
        sb.Append("\"QualityLevel\":1,\"ImageQuality\":50,\"ViewDistance\":5,\"Rtx\":0,\"Dlss\":3,\"InsertFrame\":0,\"Dx12\":1,\"Records\":[");
        for (var i = 0; i < 500; i++)
        {
            var fps = 90 + rnd.NextDouble() * 40; var ft = 1000 / fps;
            sb.Append(CultureInfo.InvariantCulture, $"{(i > 0 ? "," : "")}{{\"FrameRate\":{fps:0.00},\"CPUUsage\":80,\"GPUUsage\":40,\"CPUFrameTime\":{ft:0.000},\"GPUFrameTime\":{ft * 0.45:0.000},\"VideoMemoryUsage\":5000}}");
        }
        sb.Append("]}");
        var file = Path.Combine(dir, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
        File.WriteAllText(file, sb.ToString());
        L("result " + file);
        Set("results", "Результаты");
    };
    t.Start();
}
Application.Run(form);
