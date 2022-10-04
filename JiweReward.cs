using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

[System.Serializable]
public class DeviceInfoSerialzeClass
{
    public string gameId;
    public string amount;
    public string orderId;
    public string description;
    public string applyTransactionFeeOn;
    public string idempotencyKey;
  
}
[System.Serializable]
public class Meta
{

    public string key;
   
}
public class JiweReward : MonoBehaviour
{
    public JiweOAuth2 jiwe;
    string JiweUrl = "https://bursment.jiwe.io/api/v1/cowrie/rewards";

    // Start is called before the first frame update
    void Start()
    {
       
    }

    public string GenerateRandomString()
    {
        int needLength = 8;
        string randomStr = string.Join("", System.Guid.NewGuid().ToString("n").Take(needLength).Select(r => r));
        return randomStr;
    }

    public void RewardPlayer( double amt, string oid, string desc) 
    {
        JiweUrl = "https://bursment.jiwe.io/api/v1/cowrie/rewards";
        DeviceInfoSerialzeClass writePlayer = new DeviceInfoSerialzeClass()
        {
            gameId = jiwe.gameID,
            amount = amt.ToString(),
            orderId = oid,
            description = desc,
            idempotencyKey = GenerateRandomString(),

        };
        string jsonString = JsonUtility.ToJson(writePlayer);
        StartCoroutine(Post(JiweUrl, jsonString));
       
    }
    
    public void Purchase(double amt, string oid, string desc)
    {
        JiweUrl = "https://bursment.jiwe.io/api/v1/cowrie/purchases";
        DeviceInfoSerialzeClass writePlayer = new DeviceInfoSerialzeClass()
        {
            gameId = jiwe.gameID,
            amount = amt.ToString(),
            orderId = oid,
            description = desc,
            idempotencyKey = GenerateRandomString(),

        };
        string jsonString = JsonUtility.ToJson(writePlayer);
        StartCoroutine(Post(JiweUrl, jsonString));

    }

    IEnumerator Post(string url, string bodyJsonString)
    {

        
        var request = new UnityWebRequest(url, "POST");
        Debug.Log(bodyJsonString);
        byte[] bodyRaw = Encoding.UTF8.GetBytes(bodyJsonString);
        Debug.Log(bodyRaw);
        request.uploadHandler = (UploadHandler)new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = (DownloadHandler)new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("api_key", jiwe.testMode ? $"test_{jiwe.apiKey}" : jiwe.apiKey);
        request.SetRequestHeader("api_secret", jiwe.apiSecret);
        request.SetRequestHeader("Authorization", "Bearer " + jiwe.wallet_access_token);
        Debug.Log(jiwe.apiKey);
        Debug.Log(jiwe.apiSecret);
        Debug.Log(jiwe.wallet_access_token);
        yield return request.SendWebRequest();
        Debug.Log("Status Code: " + request.responseCode);
        Debug.Log("Response: " + request.result);
        
    }

}
