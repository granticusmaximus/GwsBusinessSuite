using FluentAssertions;

namespace GwsBusinessSuite.Tests;

// Mail drag and drop (21-11-26): rows drag, tabs and your folders take the drop, and the list's script
// hands the drop to the page, which moves the message as right-click does. The browser behaviour itself
// was checked on screen; this keeps the three pieces wired together.
public sealed class MailDragDropTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    private static string Read(string path) => File.ReadAllText(Path.Combine(Root, path));

    [Fact]
    public void Rows_ShouldDrag_TabsAndFoldersTakeTheDrop_AndThePageMovesIt()
    {
        var page = Read("src/GwsBusinessSuite.Web/Components/Pages/BusinessSuite/Mail.razor");
        var script = Read("src/GwsBusinessSuite.Web/wwwroot/js/mail-list.js");
        var styles = Read("src/GwsBusinessSuite.Web/Components/Pages/BusinessSuite/Mail.razor.css");

        page.Should().Contain("data-id=\"@message.Id\" draggable=\"true\"")
            .And.Contain("data-drop-key=\"@(current ? null : tab.Key)\"")
            .And.Contain("data-drop-key=\"@(folder.Key == _folder ? null : folder.Key)\"")
            .And.Contain("public Task OnDropMail(string id, string targetKey)");
        script.Should().Contain("invokeMethodAsync('OnDropMail', id, target.dataset.dropKey)")
            .And.Contain("if (!item || state.touch || state.active) { e.preventDefault(); return; }", "a touch swipe never turns into a drag");
        styles.Should().NotContain(":global(", "scoped CSS never uses :global()");
    }
}
