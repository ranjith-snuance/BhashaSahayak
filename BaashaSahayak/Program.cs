using Azure;
using Azure.AI.OpenAI;
using Azure.Core;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Transcription;
using OpenAI.Assistants;
using OpenAI.Chat;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Diagnostics.Metrics;
using System.Text;
using System.Threading.Tasks;


namespace BaashaSahayak
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            QuestPDF.Settings.License = LicenseType.Community;
            var endpoint = new Uri("https://consoleagent.openai.azure.com/");
            var deploymentName = "gpt-4o";
            var apiKey = "xxxx-xxx";
            var speechConfig = SpeechConfig.FromEndpoint(
            new Uri("https://eastus.api.cognitive.microsoft.com/"),
            "xxxx-xxx"
            );
            speechConfig.SpeechRecognitionLanguage = "en-US";

            using var audioConfig = AudioConfig.FromDefaultMicrophoneInput();
            using var recognizer = new SpeechRecognizer(speechConfig, audioConfig);

            var recognizedSegments = new List<string>();
            string? lastSegment = null;

            recognizer.Recognized += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Result.Text) && e.Result.Text != lastSegment)
                {
                    recognizedSegments.Add(e.Result.Text);
                    Console.WriteLine($"[Final] {e.Result.Text}");
                    lastSegment = e.Result.Text;
                }
            };

            recognizer.Canceled += (s, e) =>
            {
                Console.WriteLine($"Recognition canceled. Reason: {e.Reason}");
                if (e.Reason == CancellationReason.Error)
                {
                    Console.WriteLine($"ErrorCode: {e.ErrorCode}");
                    Console.WriteLine($"ErrorDetails: {e.ErrorDetails}");
                }
            };

            recognizer.SessionStopped += (s, e) =>
            {
                Console.WriteLine("Speech session stopped.");
            };

            Console.WriteLine("Start speaking. Press Enter when you are DONE dictating...");
            await recognizer.StartContinuousRecognitionAsync();

            Console.ReadLine();

            Console.WriteLine("Stopping recognition...");
            await recognizer.StopContinuousRecognitionAsync();

            var transcript = string.Join(" ", recognizedSegments).Trim();
            if (string.IsNullOrWhiteSpace(transcript))
            {
                Console.WriteLine("No speech captured. Exiting.");
                return;
            }

            Console.WriteLine("\n--- Raw Transcript ---");
            Console.WriteLine(transcript);
            Console.WriteLine("----------------------\n");

            var templates = new Dictionary<string, List<string>>
            {
                { "bank_account_closure", new List<string> { "Name", "Account Number", "Mobile Number", "Date", "Place" } },
                { "address_change", new List<string> { "Name", "Account Number", "Old Address", "New Address", "Date" } },
                { "aadhaar_update", new List<string> { "Name", "Aadhaar Number", "PAN Number", "Date", "Place" } }
            };

            var templateJson = System.Text.Json.JsonSerializer.Serialize(templates);

            var systemPrompt = $@"
            You are a public documentation assistant.

            Available letter templates (with required fields):
            {templateJson}

            Rules:
            1. First, identify which template best matches the user request.  
            2. Collect all required fields for that template, step by step.  
            3. Do not generate the letter until all required fields are collected.  
            4. Ask the user to specify the final output language (mandatory).  
            5. Once all required fields + language are available, generate the letter.  

            Output Rules:  
            - Output ONLY the letter text.  
            - No extra explanations or assistant chatter.  
            - Always end with [END_OF_LETTER] marker.  
            - Do not add text after [END_OF_LETTER].
            ";


            var azureClient = new AzureOpenAIClient(endpoint, new AzureKeyCredential(apiKey));
            var chatClient = azureClient.GetChatClient(deploymentName);

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(systemPrompt),
            };

            //You are a public documentation assistant.
            //Ask the user step by step for required details.
            //Once all details are collected, generate the final letter in the requested regional language.
            //Always end the letter with the marker: [END_OF_LETTER].

            // Conversation loop
            while (true)
            {
                // Send current conversation to AI
                var streamingResponse = chatClient.CompleteChatStreaming(messages);

                var assistantReply = new StringBuilder();
                foreach (var update in streamingResponse)
                {
                    foreach (var part in update.ContentUpdate)
                    {
                        if (!string.IsNullOrEmpty(part.Text))
                        {
                            Console.Write(part.Text);
                            assistantReply.Append(part.Text);
                        }
                    }
                }
                Console.WriteLine();

                // Save assistant reply into history
                messages.Add(new AssistantChatMessage(assistantReply.ToString()));

                // If the AI has generated the FINAL LETTER, break
                if (assistantReply.ToString().Contains("[END_OF_LETTER]"))
                {
                    var random = new Random().Next().ToString();
                    //Console.WriteLine("Final Letter Generated. Enter Y to generate PDF N to Continue..");
                    //string ? userInput = Console.ReadLine();
                    //if(userInput != null && userInput.ToUpper() == "Y")
                    //{
                    string kannadaLetter = assistantReply.ToString(); // AI output

                    Document.Create(container =>
                    {
                        container.Page(page =>
                        {
                            page.Size(PageSizes.A4);
                            page.Margin(2, Unit.Centimetre);
                            page.Content().Text(kannadaLetter)
                                .FontSize(12)
                                .FontFamily("Nirmala UI"); // supports Kannada
                        });
                    })
                    .GeneratePdf(random + ".pdf");

                    // Open with default PDF viewer
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
                    {
                        FileName = random + ".pdf",
                        UseShellExecute = true // important to open with associated app
                    });
                    //}


                    Console.WriteLine($"✅ Letter saved as PDF: {random}.pdf");
                    Console.WriteLine("\n--- Letter generation completed ---");
                    break;
                }

                // Capture next user speech input
                Console.WriteLine("\nYour turn, please answer...");
                await recognizer.StartContinuousRecognitionAsync();
                Console.ReadLine(); // user speaks, then press Enter
                await recognizer.StopContinuousRecognitionAsync();

                var reply = string.Join(" ", recognizedSegments).Trim();
                recognizedSegments.Clear();

                // Add user response to chat
                if (!string.IsNullOrWhiteSpace(reply))
                {
                    messages.Add(new UserChatMessage(reply));
                }
            }
        }
    }
}
