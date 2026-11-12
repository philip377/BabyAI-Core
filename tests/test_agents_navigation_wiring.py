"""Regression test: the Agents sidebar must not open the Ginger log directly."""

from pathlib import Path
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
XAML = ROOT / "desktop" / "BabyAI.Desktop" / "MainWindow.xaml"
AGENTS_CS = ROOT / "desktop" / "BabyAI.Desktop" / "MainWindow.Agents.cs"
NS_XAML = "{http://schemas.microsoft.com/winfx/2006/xaml}"


def test_agents_sidebar_opens_agents_dialog():
    tree = ET.parse(XAML)
    buttons = [
        element
        for element in tree.getroot().iter()
        if element.tag.endswith("Button")
        and element.get(f"{NS_XAML}Name") == "GingerNavigationButton"
    ]
    assert len(buttons) == 1, "Expected exactly one Agents sidebar button"
    button = buttons[0]
    assert button.get("Click") == "AgentsNavigationButton_Click", (
        "Agents sidebar must open the Agents dialog, not Ginger's journal"
    )
    assert button.get("AutomationProperties.Name") == "Агенты"


def test_agents_dialog_exposes_companion():
    agents_source = AGENTS_CS.read_text(encoding="utf-8")
    assert "void AgentsNavigationButton_Click(" in agents_source
    assert "new GingerCompanionWindow(profile," in agents_source
    assert "На рабочий стол" in agents_source


def test_ginger_agent_workspace_is_not_just_a_log():
    source = (
        ROOT / "desktop" / "BabyAI.Desktop" / "MainWindow.Ginger.cs"
    ).read_text(encoding="utf-8")
    assert 'Title = "UNIX · Ginger Agent"' in source
    assert 'Text = "Ginger Agent"' in source
    assert "void UpdateAgentStatus(GingerCompanionPhase phase)" in source
    assert 'Content = "Показать журнал действий"' in source
    assert "journal.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;" in source
    assert "SetGingerCaptureMode(true)" in source
    assert "SetGingerCaptureMode(false)" in source
