namespace C2O.Serialization
{
    public class AppConfig
    {
        public string ActiveProfile { get; set; } = "Default";
        public Dictionary<string, ProfileData> Profiles { get; set; } = new Dictionary<string, ProfileData>();
    }
}
