public class AppSession
{
    public string AppName { get; set; } = string.Empty;
    public string WindowTitle { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }

    public TimeSpan Duration => EndTime - StartTime;
}
