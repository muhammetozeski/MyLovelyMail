namespace MyLovelyMail.MainProject.UI.Elements.Modals;

/// <summary>Standard modal width tiers in the unified CoreModal hierarchy.</summary>
public enum ModalSize
{
    Compact,
    Dialog,
    Workspace,
    Wide
}

/// <summary>Base class for modal components rendered by <see cref="UI.Components.AppModalHost"/>.</summary>
public abstract class ModalBase : UI.Layout.Architecture.ThemedComponentBase
{
}
