using System.Runtime.CompilerServices;
using Torch.API.Managers;

// The namespace and the name are SEDiscordBridge's: SentisTranslator knows a message from Discord by this type on the stack.
namespace SEDiscordBridge
{
    /// <summary>
    /// A stand-in for SEDiscordBridge's <c>DiscordBridge</c> (the stand has no Discord bot): shows a "Discord" message in
    /// the game as the bridge does (<c>Discord_MessageCreated</c>: Torch's <c>SendMessageAsOther(author, message, font,
    /// target)</c>), for <c>translator_chat</c>.
    /// </summary>
    public sealed class DiscordBridge
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Discord_MessageCreated(IChatManagerServer chat, string author, string message, ulong target) =>
            chat.SendMessageAsOther(author, message, "Blue", target);
    }
}
