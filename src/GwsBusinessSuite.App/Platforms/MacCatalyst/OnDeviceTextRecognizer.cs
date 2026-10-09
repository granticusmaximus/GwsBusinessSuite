using CoreGraphics;
using Foundation;
using UIKit;
using Vision;

namespace GwsBusinessSuite.App;

// Reads the text in an image on this Mac with Apple's Vision framework - no network, no model.
// Lines come back top to bottom (Vision's normalized coordinates have their origin bottom-left).
public static class OnDeviceTextRecognizer
{
    public static Task<(string? Text, string? Error)> RecognizeAsync(UIImage image)
    {
        if (image.CGImage is not CGImage cgImage) return Task.FromResult<(string?, string?)>((null, "That image couldn't be read."));

        var done = new TaskCompletionSource<(string?, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new VNRecognizeTextRequest((request, error) =>
        {
            if (error is not null)
            {
                done.TrySetResult((null, error.LocalizedDescription));
                return;
            }
            var lines = (request.GetResults<VNRecognizedTextObservation>() ?? [])
                .OrderByDescending(o => o.BoundingBox.Y)
                .ThenBy(o => o.BoundingBox.X)
                .Select(o => o.TopCandidates(1).FirstOrDefault()?.String)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();
            done.TrySetResult(lines.Count == 0 ? (null, "No text was found in that image.") : (string.Join("\n", lines), null));
        })
        {
            RecognitionLevel = VNRequestTextRecognitionLevel.Accurate,
            UsesLanguageCorrection = true
        };

        // Vision does the work synchronously on the calling thread, so keep it off the UI thread.
        _ = Task.Run(() =>
        {
            using var handler = new VNImageRequestHandler(cgImage, new NSDictionary());
            if (!handler.Perform([request], out var performError) && performError is not null)
            {
                done.TrySetResult((null, performError.LocalizedDescription));
            }
        });
        return done.Task;
    }
}
