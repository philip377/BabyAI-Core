using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace BabyAI.Desktop;
public sealed partial class MainWindow
{
    private UIElement BuildAgentSettings()
    {
        var panel = CreateSettingsPage("Агенты и питомцы", "Внешний вид меняется независимо от выполнения задач.");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        try
        {
            var preferences = _agentStore.Load().Preferences;
            var animate = new ToggleSwitch { Header = "Анимации питомцев", IsOn = preferences.Animate };
            var quiet = new ToggleSwitch { Header = "Тихий режим — без сообщений питомцев", IsOn = preferences.Quiet };
            var fullscreen = new ToggleSwitch { Header = "Прятать в полноэкранных приложениях", IsOn = preferences.HideInFullscreen };
            void Save()
            {
                try
                {
                    var catalog = _agentStore.Load();
                    _agentStore.Save(catalog with { Preferences = new(animate.IsOn, quiet.IsOn, fullscreen.IsOn) });
                    var visible = _companions.Keys.ToArray();
                    CloseCompanions();
                    foreach (var id in visible) ShowCompanion(catalog.Agents.Single(a => a.Id == id));
                    status.Text = "Настройки сохранены.";
                }
                catch (Exception ex) { status.Text = "Не удалось сохранить: " + ex.Message; }
            }
            animate.Toggled += (_, _) => Save(); quiet.Toggled += (_, _) => Save(); fullscreen.Toggled += (_, _) => Save();
            panel.Children.Add(animate); panel.Children.Add(quiet); panel.Children.Add(fullscreen);
            panel.Children.Add(CreateSettingsNote("Питомцы скрываются на время чтения экрана Ginger. Закрытие питомца не останавливает задачу. Создание агента сохраняет специализацию, но не расширяет разрешения UNIX."));
            var resetChat = new Button { Content = "Вернуть общий чат UNIX" };
            resetChat.Click += (_, _) => { _chatAgent = null; MessageBox.PlaceholderText = "Напиши сообщение…"; status.Text = "Общий чат UNIX выбран."; };
            panel.Children.Add(resetChat);
        }
        catch (Exception ex) { status.Text = ex.Message; }
        panel.Children.Add(status);
        return panel;
    }
}
