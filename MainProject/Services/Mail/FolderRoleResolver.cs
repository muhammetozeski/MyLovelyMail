using MyLovelyMail.MainProject.DataModels.Mail;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>How sure the app is about a folder's role, strongest first.</summary>
    public enum FolderRoleEvidence
    {
        /// <summary>No role.</summary>
        None,

        /// <summary>The connection's own Inbox reference — the server said so directly.</summary>
        InboxReference,

        /// <summary>RFC 6154 SPECIAL-USE (or the older Gmail XLIST) attribute on the folder.</summary>
        SpecialUse,

        /// <summary>The folder's name matched a known name for that role, exactly.</summary>
        NameTable
    }

    /// <summary>One folder as the resolver sees it, with nothing MailKit-shaped left in it.</summary>
    /// <param name="SpecialUse">Role the server's own attributes claim, or None when it claims nothing.</param>
    public readonly record struct FolderRoleCandidate(
        string FullName,
        string DisplayName,
        FolderRole SpecialUse,
        bool IsInbox,
        int MessageCount,
        int Depth);

    /// <summary>The role a folder ended up with, and what decided it.</summary>
    public readonly record struct FolderRoleDecision(
        string FullName,
        FolderRole Role,
        FolderRoleEvidence Evidence,
        string Why);

    /// <summary>
    /// Decides which folder holds which role, for a whole account at once.
    /// <para>
    /// Two things went wrong while this was a per-folder name guess. The name table was English
    /// only, so a server answering "Gönderilmiş Postalar", "Papierkorb" or "Corbeille" produced no
    /// role at all. And nothing checked for a SECOND claimant: an account with both "Sent" and
    /// "Outbox" ended up with two folders behaving like the sent folder, which left "where does a
    /// sent message go" with two answers. Deciding per account instead of per folder is what makes
    /// "exactly one folder per role" something the code can promise rather than hope for.
    /// </para>
    /// <para>
    /// Outbox is deliberately NOT resolvable here. RFC 6154 defines All, Archive, Drafts, Flagged,
    /// Junk, Sent and Trash — there is no outbox in the protocol, because an outbox is a queue on
    /// the machine doing the sending. A server folder that happens to be named "Outbox" is just a
    /// folder, and the app's queue stays local.
    /// </para>
    /// </summary>
    public static class FolderRoleResolver
    {
        /// <summary>
        /// Assigns roles across the account's folders. Every input folder appears in the result
        /// exactly once; at most one folder per role carries that role.
        /// </summary>
        public static List<FolderRoleDecision> Resolve(IReadOnlyList<FolderRoleCandidate> candidates)
        {
            // Step 1: what each folder claims on its own.
            var claims = new List<(FolderRoleCandidate Candidate, FolderRole Role, FolderRoleEvidence Evidence)>();
            foreach (var candidate in candidates)
            {
                if (candidate.IsInbox || candidate.FullName.Equals(InboxName, StringComparison.OrdinalIgnoreCase))
                {
                    claims.Add((candidate, FolderRole.Inbox, FolderRoleEvidence.InboxReference));
                    continue;
                }
                if (candidate.SpecialUse != FolderRole.None && candidate.SpecialUse != FolderRole.Outbox)
                {
                    claims.Add((candidate, candidate.SpecialUse, FolderRoleEvidence.SpecialUse));
                    continue;
                }
                if (RoleFromName(candidate.DisplayName) is var named && named != FolderRole.None)
                {
                    claims.Add((candidate, named, FolderRoleEvidence.NameTable));
                    continue;
                }
                claims.Add((candidate, FolderRole.None, FolderRoleEvidence.None));
            }

            // Step 2: one winner per role. Strongest evidence first; then the folder nearest the
            // top of the tree, because a role folder the server itself maintains sits at the root
            // while a user's own "Archive/2024" sits under something; then the fuller folder;
            // then the name, so the answer cannot change between two runs over the same mailbox.
            List<FolderRoleDecision> decisions = [];
            var winners = claims
                .Where(static c => c.Role != FolderRole.None)
                .GroupBy(static c => c.Role)
                .ToDictionary(
                    static g => g.Key,
                    static g => g
                        .OrderBy(static c => c.Evidence == FolderRoleEvidence.InboxReference ? 0
                                           : c.Evidence == FolderRoleEvidence.SpecialUse ? 1 : 2)
                        .ThenBy(static c => c.Candidate.Depth)
                        .ThenByDescending(static c => c.Candidate.MessageCount)
                        .ThenBy(static c => c.Candidate.FullName, StringComparer.Ordinal)
                        .First());

            foreach (var (candidate, role, evidence) in claims)
            {
                if (role == FolderRole.None)
                {
                    decisions.Add(new FolderRoleDecision(candidate.FullName, FolderRole.None, FolderRoleEvidence.None, "no special-use flag and no known name"));
                    continue;
                }

                var winner = winners[role];
                if (winner.Candidate.FullName == candidate.FullName)
                {
                    decisions.Add(new FolderRoleDecision(candidate.FullName, role, evidence, WhyWon(evidence)));
                    continue;
                }

                decisions.Add(new FolderRoleDecision(candidate.FullName, FolderRole.None, FolderRoleEvidence.None,
                    $"'{winner.Candidate.FullName}' holds {role} on stronger evidence ({WhyWon(winner.Evidence)})"));
            }
            return decisions;
        }

        const string InboxName = "INBOX";

        static string WhyWon(FolderRoleEvidence evidence) => evidence switch
        {
            FolderRoleEvidence.InboxReference => "the connection's inbox",
            FolderRoleEvidence.SpecialUse => "SPECIAL-USE flag",
            FolderRoleEvidence.NameTable => "known folder name",
            _ => "nothing"
        };

        /// <summary>
        /// The role a folder name is known to mean, or None. Matched WHOLE and case-insensitively:
        /// a substring test would make "Sent to accounting" the sent folder and "Archive of 2019"
        /// the archive. Only the leaf name is looked at, so "[Gmail]/Çöp kutusu" and
        /// "INBOX.Trash" resolve exactly like a root-level folder of the same name.
        /// </summary>
        public static FolderRole RoleFromName(string leafName)
        {
            string name = leafName.Trim();
            foreach (var (role, names) in NamesByRole)
                if (names.Contains(name))
                    return role;
            return FolderRole.None;
        }

        static HashSet<string> Set(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Known folder names per role. Servers without SPECIAL-USE answer in the language their
        /// webmail was installed in, so an English-only table left most non-English mailboxes with
        /// no roles at all. Turkish spellings appear twice where the dotted capital İ is involved:
        /// case-insensitive comparison folds i/I, never İ/i.
        /// </summary>
        static readonly (FolderRole Role, HashSet<string> Names)[] NamesByRole =
        [
            (FolderRole.Sent, Set(
                "sent", "sent items", "sent mail", "sent messages", "sent-mail", "outgoing",
                "gönderilmiş postalar", "gönderilenler", "gönderilmiş", "gönderilmiş öğeler", "giden",
                "gesendet", "gesendete objekte", "gesendete elemente",
                "envoyés", "éléments envoyés", "messages envoyés",
                "enviados", "elementos enviados", "correo enviado",
                "posta inviata", "inviata", "inviati",
                "отправленные", "отправленные письма",
                "verzonden", "verzonden items",
                "itens enviados")),

            (FolderRole.Drafts, Set(
                "drafts", "draft",
                "taslaklar", "taslak",
                "entwürfe",
                "brouillons",
                "borradores",
                "bozze",
                "черновики",
                "concepten",
                "rascunhos")),

            (FolderRole.Trash, Set(
                "trash", "deleted", "deleted items", "deleted messages", "bin", "wastebasket",
                "çöp kutusu", "çöp", "silinmiş öğeler", "silinenler", "silinmiş",
                "papierkorb", "gelöschte objekte", "gelöschte elemente",
                "corbeille", "éléments supprimés",
                "papelera", "elementos eliminados",
                "cestino", "posta eliminata",
                "удаленные", "удалённые", "корзина",
                "prullenbak", "verwijderde items",
                "lixeira", "itens excluídos")),

            (FolderRole.Junk, Set(
                "junk", "spam", "junk e-mail", "junk email", "bulk mail", "spambox",
                "önemsiz", "gereksiz", "istenmeyen", "İstenmeyen", "istenmeyen posta", "İstenmeyen posta",
                "junk-e-mail",
                "courrier indésirable", "indésirables", "pourriel",
                "correo no deseado", "no deseado",
                "posta indesiderata", "indesiderata",
                "спам", "нежелательная почта",
                "ongewenste e-mail", "ongewenst",
                "lixo eletrônico")),

            (FolderRole.Archive, Set(
                "archive", "archives",
                "arşiv", "Arşiv",
                "archiv",
                "archivo",
                "archivio",
                "архив",
                "archief",
                "arquivo")),

            (FolderRole.AllMail, Set(
                "all mail", "all messages",
                "tüm postalar", "tüm mesajlar", "tüm mail",
                "alle nachrichten",
                "tous les messages",
                "todos los mensajes",
                "tutti i messaggi",
                "вся почта",
                "alle berichten",
                "todos os e-mails")),

            (FolderRole.Flagged, Set(
                "starred", "flagged",
                "yıldızlı",
                "markiert",
                "suivis", "messages suivis",
                "destacados",
                "speciali",
                "помеченные",
                "met ster",
                "com estrela"))
        ];
    }
}
