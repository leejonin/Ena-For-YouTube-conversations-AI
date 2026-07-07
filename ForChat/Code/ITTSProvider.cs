using System.Threading.Tasks;

public class TTSSynthesisRequest
{
    public string model;
    public string voice;
    public string input;
    public float speed;
    public string instructions;
    public int totalSteps;
}

public interface ITTSProvider
{
    Task<byte[]> SynthesizeAsync(TTSSynthesisRequest request, string apiKey);
}
