using System.Globalization;
using GitHub.Copilot.SDK;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace TroubleScout.UI;

internal static class ModelPickerUI
{
    internal sealed record ModelPickerChoice(
        string ModelId,
        string DisplayName,
        string RateLabel,
        string DetailSummary,
        bool IsCurrent,
        TroubleshootingSession.ModelSource? SourceHint);

    public static string? PromptModelSelection(string currentModel, IReadOnlyList<ModelInfo> models)
    {
        var choices = models
            .Select(model => new ModelPickerChoice(
                model.Id,
                model.Name,
                GetRateLabel(model),
                BuildModelDetailSummary(model),
                model.Id.Equals(currentModel, StringComparison.OrdinalIgnoreCase),
                null))
            .ToList();

        return PromptModelSelectionCore(currentModel, choices)?.ModelId;
    }

    internal static TroubleshootingSession.ModelSelectionEntry? PromptModelSelection(
        string currentModel,
        IReadOnlyList<TroubleshootingSession.ModelSelectionEntry> entries)
    {
        var choices = entries
            .Select(entry => new ModelPickerChoice(
                entry.ModelId,
                entry.DisplayName,
                entry.RateLabel,
                entry.DetailSummary,
                entry.IsCurrent,
                entry.Source))
            .ToList();

        var selectedChoice = PromptModelSelectionCore(currentModel, choices);
        if (selectedChoice == null)
        {
            return null;
        }

        return entries.FirstOrDefault(entry =>
            entry.ModelId.Equals(selectedChoice.ModelId, StringComparison.OrdinalIgnoreCase)
            && entry.Source == selectedChoice.SourceHint);
    }

    internal static TroubleshootingSession.ModelSwitchBehavior? PromptModelSwitchBehavior(
        string currentModel,
        string selectedModel)
    {
        if (ConsoleUI.IsInputRedirectedResolver())
        {
            return TroubleshootingSession.ModelSwitchBehavior.CleanSession;
        }

        const string cleanLabel = "Start a new clean session";
        const string secondOpinionLabel =
            "Ask another model for a second opinion using the full conversation and tool outputs (they will all be sent to it)";
        const string cancelLabel = "Cancel";
        IReadOnlyList<string> choices = [cleanLabel, secondOpinionLabel, cancelLabel];

        var title =
            $"[bold cyan]Switch to {Markup.Escape(selectedModel)}[/]{Environment.NewLine}" +
            $"[grey]Current model:[/] [cyan]{Markup.Escape(currentModel)}[/]{Environment.NewLine}" +
            "[grey]Choose whether to start fresh or share the current conversation and tool outputs with the selected model.[/]";

        LiveThinkingIndicator.PauseForApproval();
        try
        {
            var selected = ConsoleUI.ModelSwitchBehaviorPromptOverride != null
                ? ConsoleUI.ModelSwitchBehaviorPromptOverride(title, choices)
                : AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title(title)
                        .PageSize(3)
                        .AddChoices(choices)
                        .UseConverter(choice => choice switch
                        {
                            cleanLabel => $"[green]{Markup.Escape(choice)}[/]",
                            secondOpinionLabel => $"[cyan]{Markup.Escape(choice)}[/]",
                            _ => $"[grey]{Markup.Escape(choice)}[/]"
                        }));

            return selected switch
            {
                cleanLabel => TroubleshootingSession.ModelSwitchBehavior.CleanSession,
                secondOpinionLabel => TroubleshootingSession.ModelSwitchBehavior.SecondOpinion,
                _ => null
            };
        }
        finally
        {
            LiveThinkingIndicator.ResumeAfterApproval();
        }
    }

    public static string? PromptReasoningEffort(
        string? currentReasoningEffort,
        IReadOnlyList<string> supportedEfforts,
        string? defaultReasoningEffort)
    {
        if (ConsoleUI.IsInputRedirectedResolver() || supportedEfforts.Count == 0)
        {
            return currentReasoningEffort;
        }

        var automaticLabel = string.IsNullOrWhiteSpace(defaultReasoningEffort)
            ? "Automatic"
            : $"Automatic (default: {defaultReasoningEffort})";

        var choices = new List<string> { automaticLabel };
        choices.AddRange(supportedEfforts);

        var prompt = new SelectionPrompt<string>()
            .Title("[bold cyan]Select reasoning effort[/]")
            .PageSize(Math.Min(choices.Count, 8))
            .AddChoices(choices)
            .UseConverter(choice => choice == automaticLabel
                ? $"[green]{Markup.Escape(choice)}[/]"
                : $"[cyan]{Markup.Escape(choice)}[/]");

        var selected = AnsiConsole.Prompt(prompt);
        return selected == automaticLabel ? null : selected;
    }

    internal static ModelPickerChoice? PromptModelSelectionCore(string currentModel, IReadOnlyList<ModelPickerChoice> choices)
    {
        if (choices.Count == 0)
        {
            return null;
        }

        if (ConsoleUI.IsInputRedirectedResolver())
        {
            return choices.FirstOrDefault(choice => choice.IsCurrent) ?? choices[0];
        }

        var selectedIndex = 0;
        for (var i = 0; i < choices.Count; i++)
        {
            if (choices[i].IsCurrent)
            {
                selectedIndex = i;
                break;
            }
        }

        ModelPickerChoice? result = null;

        AnsiConsole.Live(BuildModelPickerLayout(currentModel, choices, selectedIndex))
            .Overflow(VerticalOverflow.Ellipsis)
            .Start(context =>
            {
                while (true)
                {
                    context.UpdateTarget(BuildModelPickerLayout(currentModel, choices, selectedIndex));

                    var key = Console.ReadKey(intercept: true).Key;
                    switch (key)
                    {
                        case ConsoleKey.UpArrow:
                        case ConsoleKey.K:
                            selectedIndex = (selectedIndex - 1 + choices.Count) % choices.Count;
                            break;
                        case ConsoleKey.DownArrow:
                        case ConsoleKey.J:
                            selectedIndex = (selectedIndex + 1) % choices.Count;
                            break;
                        case ConsoleKey.PageUp:
                            selectedIndex = Math.Max(0, selectedIndex - 5);
                            break;
                        case ConsoleKey.PageDown:
                            selectedIndex = Math.Min(choices.Count - 1, selectedIndex + 5);
                            break;
                        case ConsoleKey.Enter:
                            result = choices[selectedIndex];
                            return;
                        case ConsoleKey.Escape:
                            result = choices.FirstOrDefault(choice => choice.IsCurrent) ?? choices[selectedIndex];
                            return;
                    }
                }
            });

        AnsiConsole.WriteLine();
        return result;
    }

    internal static IRenderable BuildModelPickerLayout(string currentModel, IReadOnlyList<ModelPickerChoice> choices, int selectedIndex)
    {
        var visibleRange = GetVisibleModelPickerRange(choices.Count, selectedIndex, GetModelPickerPageSize());
        var selectedChoice = choices[selectedIndex];
        var showSourceColumn = choices.Any(choice => choice.SourceHint.HasValue);

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Expand()
            .AddColumn(new TableColumn("[bold] [/]").Width(2))
            .AddColumn(new TableColumn("[bold]Model[/]"));

        if (showSourceColumn)
        {
            table.AddColumn(new TableColumn("[bold]Source[/]"));
        }

        table.AddColumn(new TableColumn("[bold]Rate[/]").RightAligned());

        foreach (var index in Enumerable.Range(visibleRange.StartIndex, visibleRange.Count))
        {
            var choice = choices[index];
            var pointer = index == selectedIndex ? "[cyan]>[/]" : " ";
            var name = index == selectedIndex
                ? $"[bold cyan]{Markup.Escape(choice.DisplayName)}[/]"
                : Markup.Escape(choice.DisplayName);

            if (choice.IsCurrent)
            {
                name += " [grey](active)[/]";
            }

            var sourceLabel = choice.SourceHint switch
            {
                TroubleshootingSession.ModelSource.Byok => "[yellow]BYOK[/]",
                TroubleshootingSession.ModelSource.GitHub => "[green]GitHub[/]",
                _ => "[grey]--[/]"
            };

            if (showSourceColumn)
            {
                table.AddRow(pointer, name, sourceLabel, Markup.Escape(choice.RateLabel));
            }
            else
            {
                table.AddRow(pointer, name, Markup.Escape(choice.RateLabel));
            }
        }

        var detailsGrid = new Grid();
        detailsGrid.AddColumn(new GridColumn().PadRight(1));
        detailsGrid.AddColumn();
        detailsGrid.AddRow("[grey]Current:[/]", $"[magenta]{Markup.Escape(currentModel)}[/]");
        detailsGrid.AddRow("[grey]Selection:[/]", $"[bold cyan]{Markup.Escape(selectedChoice.DisplayName)}[/]");
        detailsGrid.AddRow("[grey]Rate:[/]", $"[cyan]{Markup.Escape(selectedChoice.RateLabel)}[/]");
        detailsGrid.AddRow("[grey]Details:[/]", $"[grey]{Markup.Escape(selectedChoice.DetailSummary)}[/]");
        detailsGrid.AddRow("[grey]Visible:[/]", $"[grey]{visibleRange.StartIndex + 1}-{visibleRange.StartIndex + visibleRange.Count} of {choices.Count}[/]");

        var detailsPanel = new Panel(detailsGrid)
            .Header("[bold cyan] Model details [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey)
            .Expand();

        var instructions = new Markup("[grey]Use [cyan]Up/Down[/] to browse, [green]Enter[/] to select, [yellow]Esc[/] to keep the current model.[/]");

        return new Rows(
            new Panel(table)
                .Header("[bold cyan] Select AI model [/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Cyan)
                .Expand(),
            detailsPanel,
            instructions);
    }

    internal static string BuildModelDetailSummary(ModelInfo model)
    {
        var details = new List<string>();

        if (model.Capabilities?.Limits?.MaxContextWindowTokens is int contextWindow && contextWindow > 0)
        {
            details.Add($"context {FormatCompactTokenCount(contextWindow)}");
        }

        if (model.Capabilities?.Limits?.MaxPromptTokens is int maxPrompt && maxPrompt > 0)
        {
            details.Add($"prompt {FormatCompactTokenCount(maxPrompt)}");
        }

        if (model.Capabilities?.Supports?.Vision == true)
        {
            details.Add("vision");
        }

        if (model.Capabilities?.Supports?.ReasoningEffort == true)
        {
            details.Add("reasoning");
        }

        if (!string.IsNullOrWhiteSpace(model.DefaultReasoningEffort))
        {
            details.Add($"default reasoning {model.DefaultReasoningEffort}");
        }

        return details.Count == 0 ? "No extra metadata available" : string.Join(" | ", details);
    }

    internal static string FormatCompactTokenCount(int value)
    {
        if (value >= 1_000_000)
        {
            return $"{value / 1_000_000d:0.#}M";
        }

        if (value >= 1_000)
        {
            return $"{value / 1_000d:0.#}k";
        }

        return value.ToString("N0", CultureInfo.InvariantCulture);
    }

    internal static int GetModelPickerPageSize()
    {
        var height = Console.WindowHeight;
        if (height <= 0)
        {
            return 8;
        }

        return Math.Max(5, height - 14);
    }

    internal static (int StartIndex, int Count) GetVisibleModelPickerRange(int totalCount, int selectedIndex, int pageSize)
    {
        if (totalCount <= 0)
        {
            return (0, 0);
        }

        pageSize = Math.Max(1, Math.Min(pageSize, totalCount));
        selectedIndex = Math.Max(0, Math.Min(selectedIndex, totalCount - 1));

        var startIndex = Math.Max(0, selectedIndex - (pageSize / 2));
        if (startIndex + pageSize > totalCount)
        {
            startIndex = Math.Max(0, totalCount - pageSize);
        }

        return (startIndex, pageSize);
    }

    public static void ShowModelSelectionSummary(string selectedModel, IReadOnlyList<(string Label, string Value)> details)
    {
        var grid = new Grid();
        grid.AddColumn(new GridColumn().PadRight(2));
        grid.AddColumn();
        grid.AddRow("[grey]Model:[/]", $"[bold magenta]{Markup.Escape(selectedModel)}[/]");

        foreach (var (label, value) in details)
        {
            if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var markup = label.StartsWith("Context", StringComparison.OrdinalIgnoreCase)
                ? $"[bold cyan]{Markup.Escape(value)}[/]"
                : $"[cyan]{Markup.Escape(value)}[/]";

            grid.AddRow($"[grey]{Markup.Escape(label)}:[/]", markup);
        }

        var panel = new Panel(grid)
            .Header("[bold green] Model selected [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Green);

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    internal static string GetRateLabel(ModelInfo model)
    {
        if (model.Billing != null)
        {
            return $"{model.Billing.Multiplier.ToString("0.##", CultureInfo.InvariantCulture)}x premium";
        }

        return "n/a";
    }
}
