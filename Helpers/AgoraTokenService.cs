using AgoraIO.Media;

public class AgoraTokenService
{
    public static string GenerateToken(
        string appId,
        string appCertificate,
        string channelName,
        uint uid,
        int expireSeconds = 3600)
    {
        var privilegeExpiredTs = (uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expireSeconds);

        var token = new AccessToken(appId, appCertificate, channelName, uid.ToString());
        token.addPrivilege(Privileges.kJoinChannel, privilegeExpiredTs);
        token.addPrivilege(Privileges.kPublishAudioStream, privilegeExpiredTs);
        token.addPrivilege(Privileges.kPublishVideoStream, privilegeExpiredTs);

        return token.build();
    }
}