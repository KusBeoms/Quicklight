using System.Windows;
using Quicklight.Core;
using Windows.Media.SpeechRecognition;

namespace Quicklight.Siri;

/// <summary>One-shot dictation with Windows speech recognition, for the microphone buttons (launcher capsule, conversation input).</summary>
public sealed class VoiceInput
{
    SpeechRecognizer? _recognizer;

    public bool Listening => _recognizer is not null;

    /// <summary>Raised when listening starts or ends.</summary>
    public event Action? Changed;

    /// <summary>Listens until something is said and returns it; null when stopped, nothing was heard, or speech is unavailable (then the user is told why).</summary>
    public async Task<string?> ListenAsync(Window? owner)
    {
        if (_recognizer is not null) { Stop(); return null; }
        SpeechRecognizer rec;
        try { rec = new SpeechRecognizer(new Windows.Globalization.Language("ko-KR")); }
        catch (Exception) { rec = new SpeechRecognizer(); } // no Korean speech pack: the system language
        _recognizer = rec;
        Changed?.Invoke();
        try
        {
            await rec.CompileConstraintsAsync();
            var result = await rec.RecognizeAsync();
            return _recognizer == rec && result.Status == SpeechRecognitionResultStatus.Success && result.Text.Length > 0 ? result.Text : null;
        }
        catch (Exception ex) when (unchecked((uint)ex.HResult) == 0x80045509 || ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // The speech privacy switch is off, or the microphone is blocked.
            Log.Info("speech recognition unavailable: " + ex.Message);
            MessageBox.Show(owner, "음성 입력을 쓰려면 Windows 설정 > 개인 정보 및 보안 > 음성에서 '온라인 음성 인식'을 켜고, 마이크 접근을 허용하세요.",
                "Quicklight", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("speech recognition failed", ex);
            return null;
        }
        finally
        {
            if (_recognizer == rec) Stop();
            else try { rec.Dispose(); } catch { } // a newer listen owns the field now: only release ours
        }
    }

    public void Stop()
    {
        var rec = _recognizer;
        _recognizer = null;
        try { rec?.Dispose(); } catch { }
        if (rec is not null) Changed?.Invoke();
    }
}
