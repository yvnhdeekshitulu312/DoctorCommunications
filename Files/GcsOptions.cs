// GcsOptions.cs
// Bound from the "Gcs" section of appsettings.json / appsettings.{Environment}.json.
public class GcsOptions
{
    public string BucketName { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string CredentialsPath { get; set; } = "";

    // Root prefix inside the bucket for everything this feature writes, kept
    // separate from any other app's use of the same bucket so a future
    // cleanup/lifecycle rule can target just this prefix.
    public string BaseFolder { get; set; } = "DoctorCommunications/SharedDocuments";
}
