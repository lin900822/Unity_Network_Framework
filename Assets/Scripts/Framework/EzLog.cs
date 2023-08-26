using UnityEngine;

public enum LogType
{
    Debug,
    Normal,
    Warning,
    Error
}

public class EzLog
{
    public static void Log(LogType type = LogType.Debug, string message = "" ,string category = "Default")
    {
        string color = "white";
        switch (type)
        {
            case LogType.Debug:
                color = "green";
                break;
            case LogType.Normal:
                color = "cyan";
                break;
            case LogType.Warning:
                color = "yellow";
                break;
            case LogType.Error:
                color = "red";
                break;
        }

        string text = $"<b>{category} </b><color={color}> {message} </color>";
        
        if(type == LogType.Warning)
            Debug.LogWarning(text);
        else if(type == LogType.Error)
            Debug.LogError(text);
        else
            Debug.Log(text);
    }
}