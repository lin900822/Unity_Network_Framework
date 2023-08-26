using Framework;
using Google.Protobuf;
using Protocol;
using TMPro;
using UnityEngine;
using Ping = UnityEngine.Ping;

namespace DefaultNamespace
{
    public class TestNetworkManager : MonoBehaviour
    {
        [SerializeField] private string ip;
        [SerializeField] private int port;

        [SerializeField] private TMP_InputField _inputField;
        [SerializeField] private TMP_Text       _text;
        
        private NetworkConnector _networkConnector;

        private string messageStr = "";

        private void Start()
        {
            _networkConnector = new NetworkConnector();
            
            _networkConnector.AddMessageHandler(typeof(Move), HandleMove);
            _networkConnector.AddMessageHandler(typeof(Pong), HandlePong);
        }

        private void Update()
        {
            _networkConnector.UpdateLogic();
        }

        private void HandleMove(IMessage message)
        {
            Move move = (Move)message;
            messageStr += $"X: {move.X} Y: {move.Y} \n";
            _text.text =  messageStr;
        }

        private void HandlePong(IMessage message)
        {
            
        }

        public void Connect()
        {
            _networkConnector.Connect(ip, port);
        }
        
        public void Disconnect()
        {
            _networkConnector.Close();
        }

        public void SendUser()
        {
            var  x    = float.Parse(_inputField.text);
            Move move = new Move();
            move.X = x;
            move.Y = 200;
            _networkConnector.Send(move);
        }
    }
}