using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public sealed class SqliteAppDataStore
{
    private const string AppearanceThemePresetsKey = "appearance_theme_presets_json";
    private const string AiApiKeySettingKey = "ai_api_key";
    private const string SyncApiTokenSettingKey = "sync_api_token";
    private readonly string _rootPath;
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly DesktopSecretStore _secretStore;

    public SqliteAppDataStore()
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "QuickReplyTool"))
    {
    }

    internal SqliteAppDataStore(string rootPath)
    {
        _rootPath = string.IsNullOrWhiteSpace(rootPath)
            ? throw new ArgumentException("Root path is required.", nameof(rootPath))
            : rootPath;
        _databasePath = Path.Combine(_rootPath, "quick-reply.db");
        _connectionString = $"Data Source={_databasePath}";
        _secretStore = new DesktopSecretStore(_rootPath);
    }

    public string DataRootPath => _rootPath;

    public string DatabasePath => _databasePath;

    public async Task<AppData> LoadAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);

        await using var connection = await OpenConnectionAsync();
        await InitializeSchemaAsync(connection);
        await SeedIfNeededAsync(connection);

        var categories = await LoadCategoriesAsync(connection);
        var phrases = await LoadPhrasesAsync(connection);
        var quickTags = await LoadQuickTagsAsync(connection);
        var fileItems = await LoadFileItemsAsync(connection);

        var phraseLookup = phrases
            .GroupBy(phrase => phrase.CategoryId)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.SortOrder).ToList());

        foreach (var category in categories)
        {
            category.Phrases = new ObservableCollection<ReplyPhrase>(
                phraseLookup.TryGetValue(category.Id, out var items) ? items : []);
        }

        return new AppData
        {
            Categories = new ObservableCollection<ReplyCategory>(categories.OrderBy(item => item.SortOrder)),
            QuickTags = new ObservableCollection<QuickTag>(quickTags.OrderBy(item => item.SortOrder).ThenBy(item => item.CreatedAt)),
            FileItems = new ObservableCollection<LibraryFileItem>(fileItems.OrderBy(item => item.DisplayName).ThenBy(item => item.CreatedAt)),
            Settings = await LoadSettingsAsync(connection)
        };
    }

    public async Task<AppSettings> LoadSettingsOnlyAsync()
    {
        if (!File.Exists(_databasePath))
        {
            return new AppSettings();
        }

        try
        {
            await using var connection = await OpenConnectionAsync();
            return await LoadSettingsAsync(connection);
        }
        catch
        {
            return new AppSettings();
        }
    }

    public async Task<IReadOnlyList<PublishedKnowledgeCard>> LoadPublishedKnowledgeCardsAsync()
    {
        if (!File.Exists(_databasePath))
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync();
        await InitializeSchemaAsync(connection);
        return await LoadPublishedKnowledgeCardsAsync(connection);
    }

    public async Task UpsertPublishedKnowledgeCardsAsync(IReadOnlyCollection<PublishedKnowledgeCard> cards)
    {
        if (cards.Count == 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync();
        await InitializeSchemaAsync(connection);
        using var transaction = connection.BeginTransaction();

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO knowledge_cards (
                id,
                candidate_kind,
                platform,
                product_key,
                question,
                answer,
                synonyms_json,
                tags_json,
                usage_notes,
                version,
                updated_at)
            VALUES (
                $id,
                $candidateKind,
                $platform,
                $productKey,
                $question,
                $answer,
                $synonymsJson,
                $tagsJson,
                $usageNotes,
                $version,
                $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                candidate_kind = excluded.candidate_kind,
                platform = excluded.platform,
                product_key = excluded.product_key,
                question = excluded.question,
                answer = excluded.answer,
                synonyms_json = excluded.synonyms_json,
                tags_json = excluded.tags_json,
                usage_notes = excluded.usage_notes,
                version = excluded.version,
                updated_at = excluded.updated_at;
            """;

        var idParameter = command.Parameters.Add("$id", SqliteType.Text);
        var candidateKindParameter = command.Parameters.Add("$candidateKind", SqliteType.Text);
        var platformParameter = command.Parameters.Add("$platform", SqliteType.Text);
        var productKeyParameter = command.Parameters.Add("$productKey", SqliteType.Text);
        var questionParameter = command.Parameters.Add("$question", SqliteType.Text);
        var answerParameter = command.Parameters.Add("$answer", SqliteType.Text);
        var synonymsJsonParameter = command.Parameters.Add("$synonymsJson", SqliteType.Text);
        var tagsJsonParameter = command.Parameters.Add("$tagsJson", SqliteType.Text);
        var usageNotesParameter = command.Parameters.Add("$usageNotes", SqliteType.Text);
        var versionParameter = command.Parameters.Add("$version", SqliteType.Text);
        var updatedAtParameter = command.Parameters.Add("$updatedAt", SqliteType.Text);

        foreach (var card in cards)
        {
            idParameter.Value = card.Id.ToString();
            candidateKindParameter.Value = string.IsNullOrWhiteSpace(card.CandidateKind) ? "qa" : card.CandidateKind.Trim();
            platformParameter.Value = card.Platform;
            productKeyParameter.Value = card.ProductKey;
            questionParameter.Value = card.Question;
            answerParameter.Value = card.Answer;
            synonymsJsonParameter.Value = JsonSerializer.Serialize(card.Synonyms);
            tagsJsonParameter.Value = JsonSerializer.Serialize(card.Tags);
            usageNotesParameter.Value = card.UsageNotes;
            versionParameter.Value = card.Version;
            updatedAtParameter.Value = ToDbDateTime(card.UpdatedAt);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task DeletePublishedKnowledgeCardsAsync(IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync();
        await InitializeSchemaAsync(connection);
        using var transaction = connection.BeginTransaction();

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM knowledge_cards WHERE id = $id;";
        var idParameter = command.Parameters.Add("$id", SqliteType.Text);

        foreach (var id in ids)
        {
            idParameter.Value = id.ToString();
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task InsertCategoryAsync(ReplyCategory category)
    {
        const string sql = """
            INSERT INTO categories (id, name, sort_order, created_at, updated_at)
            VALUES ($id, $name, $sortOrder, $createdAt, $updatedAt);
            """;

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", category.Id.ToString());
        command.Parameters.AddWithValue("$name", category.Name);
        command.Parameters.AddWithValue("$sortOrder", category.SortOrder);
        command.Parameters.AddWithValue("$createdAt", ToDbDateTime(category.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", ToDbDateTime(category.UpdatedAt));

        await command.ExecuteNonQueryAsync();
    }

    public async Task UpdateCategoryAsync(ReplyCategory category)
    {
        category.UpdatedAt = DateTimeOffset.Now;

        const string sql = """
            UPDATE categories
            SET name = $name,
                sort_order = $sortOrder,
                updated_at = $updatedAt
            WHERE id = $id;
            """;

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", category.Id.ToString());
        command.Parameters.AddWithValue("$name", category.Name);
        command.Parameters.AddWithValue("$sortOrder", category.SortOrder);
        command.Parameters.AddWithValue("$updatedAt", ToDbDateTime(category.UpdatedAt));

        await command.ExecuteNonQueryAsync();
    }

    public async Task<bool> CategoryHasPhrasesAsync(Guid categoryId)
    {
        const string sql = "SELECT COUNT(1) FROM phrases WHERE category_id = $categoryId;";

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$categoryId", categoryId.ToString());

        var count = (long)(await command.ExecuteScalarAsync() ?? 0L);
        return count > 0;
    }

    public async Task DeleteCategoryAsync(Guid categoryId)
    {
        const string sql = "DELETE FROM categories WHERE id = $id;";

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", categoryId.ToString());

        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteCategoryWithPhrasesAsync(Guid categoryId)
    {
        await using var connection = await OpenConnectionAsync();
        using var transaction = connection.BeginTransaction();

        await using (var deletePhrasesCommand = connection.CreateCommand())
        {
            deletePhrasesCommand.Transaction = transaction;
            deletePhrasesCommand.CommandText = "DELETE FROM phrases WHERE category_id = $categoryId;";
            deletePhrasesCommand.Parameters.AddWithValue("$categoryId", categoryId.ToString());
            await deletePhrasesCommand.ExecuteNonQueryAsync();
        }

        await using (var deleteCategoryCommand = connection.CreateCommand())
        {
            deleteCategoryCommand.Transaction = transaction;
            deleteCategoryCommand.CommandText = "DELETE FROM categories WHERE id = $id;";
            deleteCategoryCommand.Parameters.AddWithValue("$id", categoryId.ToString());
            await deleteCategoryCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task InsertPhraseAsync(ReplyPhrase phrase)
    {
        const string sql = """
            INSERT INTO phrases (id, category_id, title, tags, content, image_relative_path, sort_order, created_at, updated_at)
            VALUES ($id, $categoryId, $title, $tags, $content, $imageRelativePath, $sortOrder, $createdAt, $updatedAt);
            """;

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", phrase.Id.ToString());
        command.Parameters.AddWithValue("$categoryId", phrase.CategoryId.ToString());
        command.Parameters.AddWithValue("$title", phrase.Title);
        command.Parameters.AddWithValue("$tags", phrase.Tags);
        command.Parameters.AddWithValue("$content", phrase.Content);
        command.Parameters.AddWithValue("$imageRelativePath", phrase.ImageRelativePath);
        command.Parameters.AddWithValue("$sortOrder", phrase.SortOrder);
        command.Parameters.AddWithValue("$createdAt", ToDbDateTime(phrase.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", ToDbDateTime(phrase.UpdatedAt));

        await command.ExecuteNonQueryAsync();
    }

    public async Task UpdatePhraseAsync(ReplyPhrase phrase)
    {
        phrase.UpdatedAt = DateTimeOffset.Now;

        const string sql = """
            UPDATE phrases
            SET category_id = $categoryId,
                title = $title,
                tags = $tags,
                content = $content,
                image_relative_path = $imageRelativePath,
                sort_order = $sortOrder,
                updated_at = $updatedAt
            WHERE id = $id;
            """;

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", phrase.Id.ToString());
        command.Parameters.AddWithValue("$categoryId", phrase.CategoryId.ToString());
        command.Parameters.AddWithValue("$title", phrase.Title);
        command.Parameters.AddWithValue("$tags", phrase.Tags);
        command.Parameters.AddWithValue("$content", phrase.Content);
        command.Parameters.AddWithValue("$imageRelativePath", phrase.ImageRelativePath);
        command.Parameters.AddWithValue("$sortOrder", phrase.SortOrder);
        command.Parameters.AddWithValue("$updatedAt", ToDbDateTime(phrase.UpdatedAt));

        await command.ExecuteNonQueryAsync();
    }

    public async Task UpdatePhraseOrderAsync(IReadOnlyCollection<ReplyPhrase> phrases)
    {
        if (phrases.Count == 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync();
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE phrases
            SET sort_order = $sortOrder,
                updated_at = $updatedAt
            WHERE id = $id;
            """;

        var phraseIdParameter = command.Parameters.Add("$id", SqliteType.Text);
        var sortOrderParameter = command.Parameters.Add("$sortOrder", SqliteType.Integer);
        var updatedAtParameter = command.Parameters.Add("$updatedAt", SqliteType.Text);

        foreach (var phrase in phrases)
        {
            phrase.UpdatedAt = DateTimeOffset.Now;
            phraseIdParameter.Value = phrase.Id.ToString();
            sortOrderParameter.Value = phrase.SortOrder;
            updatedAtParameter.Value = ToDbDateTime(phrase.UpdatedAt);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task DeletePhraseAsync(Guid phraseId)
    {
        const string sql = "DELETE FROM phrases WHERE id = $id;";

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", phraseId.ToString());

        await command.ExecuteNonQueryAsync();
    }

    public async Task BulkImportPhrasesAsync(
        IReadOnlyCollection<ReplyCategory> categories,
        IReadOnlyCollection<ReplyPhrase> phrases,
        IProgress<int>? progress = null)
    {
        await using var connection = await OpenConnectionAsync();
        using var transaction = connection.BeginTransaction();

        var totalOperations = categories.Count + phrases.Count;
        var completedOperations = 0;

        if (categories.Count > 0)
        {
            await using var categoryCommand = connection.CreateCommand();
            categoryCommand.Transaction = transaction;
            categoryCommand.CommandText = """
                INSERT INTO categories (id, name, sort_order, created_at, updated_at)
                VALUES ($id, $name, $sortOrder, $createdAt, $updatedAt);
                """;

            var categoryIdParameter = categoryCommand.Parameters.Add("$id", SqliteType.Text);
            var categoryNameParameter = categoryCommand.Parameters.Add("$name", SqliteType.Text);
            var categorySortOrderParameter = categoryCommand.Parameters.Add("$sortOrder", SqliteType.Integer);
            var categoryCreatedAtParameter = categoryCommand.Parameters.Add("$createdAt", SqliteType.Text);
            var categoryUpdatedAtParameter = categoryCommand.Parameters.Add("$updatedAt", SqliteType.Text);

            foreach (var category in categories)
            {
                categoryIdParameter.Value = category.Id.ToString();
                categoryNameParameter.Value = category.Name;
                categorySortOrderParameter.Value = category.SortOrder;
                categoryCreatedAtParameter.Value = ToDbDateTime(category.CreatedAt);
                categoryUpdatedAtParameter.Value = ToDbDateTime(category.UpdatedAt);
                await categoryCommand.ExecuteNonQueryAsync();

                completedOperations++;
                progress?.Report(totalOperations == 0 ? 100 : completedOperations * 100 / totalOperations);
            }
        }

        if (phrases.Count > 0)
        {
            await using var phraseCommand = connection.CreateCommand();
            phraseCommand.Transaction = transaction;
            phraseCommand.CommandText = """
                INSERT INTO phrases (id, category_id, title, tags, content, image_relative_path, sort_order, created_at, updated_at)
                VALUES ($id, $categoryId, $title, $tags, $content, $imageRelativePath, $sortOrder, $createdAt, $updatedAt);
                """;

            var phraseIdParameter = phraseCommand.Parameters.Add("$id", SqliteType.Text);
            var phraseCategoryIdParameter = phraseCommand.Parameters.Add("$categoryId", SqliteType.Text);
            var phraseTitleParameter = phraseCommand.Parameters.Add("$title", SqliteType.Text);
            var phraseTagsParameter = phraseCommand.Parameters.Add("$tags", SqliteType.Text);
            var phraseContentParameter = phraseCommand.Parameters.Add("$content", SqliteType.Text);
            var phraseImageRelativePathParameter = phraseCommand.Parameters.Add("$imageRelativePath", SqliteType.Text);
            var phraseSortOrderParameter = phraseCommand.Parameters.Add("$sortOrder", SqliteType.Integer);
            var phraseCreatedAtParameter = phraseCommand.Parameters.Add("$createdAt", SqliteType.Text);
            var phraseUpdatedAtParameter = phraseCommand.Parameters.Add("$updatedAt", SqliteType.Text);

            foreach (var phrase in phrases)
            {
                phraseIdParameter.Value = phrase.Id.ToString();
                phraseCategoryIdParameter.Value = phrase.CategoryId.ToString();
                phraseTitleParameter.Value = phrase.Title;
                phraseTagsParameter.Value = phrase.Tags;
                phraseContentParameter.Value = phrase.Content;
                phraseImageRelativePathParameter.Value = phrase.ImageRelativePath;
                phraseSortOrderParameter.Value = phrase.SortOrder;
                phraseCreatedAtParameter.Value = ToDbDateTime(phrase.CreatedAt);
                phraseUpdatedAtParameter.Value = ToDbDateTime(phrase.UpdatedAt);
                await phraseCommand.ExecuteNonQueryAsync();

                completedOperations++;
                if (completedOperations % 20 == 0 || completedOperations == totalOperations)
                {
                    progress?.Report(totalOperations == 0 ? 100 : completedOperations * 100 / totalOperations);
                }
            }
        }

        await transaction.CommitAsync();
        progress?.Report(100);
    }

    public async Task UpsertFileItemAsync(LibraryFileItem fileItem)
    {
        fileItem.UpdatedAt = DateTimeOffset.Now;

        const string sql = """
            INSERT INTO file_items (id, display_name, full_path, created_at, updated_at)
            VALUES ($id, $displayName, $fullPath, $createdAt, $updatedAt)
            ON CONFLICT(full_path) DO UPDATE SET
                display_name = excluded.display_name,
                updated_at = excluded.updated_at;
            """;

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", fileItem.Id.ToString());
        command.Parameters.AddWithValue("$displayName", fileItem.DisplayName);
        command.Parameters.AddWithValue("$fullPath", fileItem.FullPath);
        command.Parameters.AddWithValue("$createdAt", ToDbDateTime(fileItem.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", ToDbDateTime(fileItem.UpdatedAt));
        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteFileItemAsync(Guid fileItemId)
    {
        const string sql = "DELETE FROM file_items WHERE id = $id;";

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", fileItemId.ToString());
        await command.ExecuteNonQueryAsync();
    }

    public async Task InsertQuickTagAsync(QuickTag quickTag)
    {
        const string sql = """
            INSERT INTO quick_tags (id, phrase_id, sort_order, created_at)
            VALUES ($id, $phraseId, $sortOrder, $createdAt);
            """;

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", quickTag.Id.ToString());
        command.Parameters.AddWithValue("$phraseId", quickTag.PhraseId.ToString());
        command.Parameters.AddWithValue("$sortOrder", quickTag.SortOrder);
        command.Parameters.AddWithValue("$createdAt", ToDbDateTime(quickTag.CreatedAt));

        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteQuickTagAsync(Guid quickTagId)
    {
        const string sql = "DELETE FROM quick_tags WHERE id = $id;";

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", quickTagId.ToString());

        await command.ExecuteNonQueryAsync();
    }

    public async Task UpdateQuickTagOrderAsync(IReadOnlyCollection<QuickTag> quickTags)
    {
        if (quickTags.Count == 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync();
        using var transaction = connection.BeginTransaction();

        foreach (var quickTag in quickTags)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE quick_tags
                SET sort_order = $sortOrder
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", quickTag.Id.ToString());
            command.Parameters.AddWithValue("$sortOrder", quickTag.SortOrder);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task UpdateSettingsAsync(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);

        var settingsMap = new Dictionary<string, string>
        {
            ["send_hotkey_mode"] = settings.SendHotkeyMode.ToString(),
            ["search_focus_hotkey_mode"] = settings.SearchFocusHotkeyMode.ToString(),
            ["sidebar_work_mode"] = settings.SidebarWorkMode.ToString(),
            ["launch_at_startup"] = settings.LaunchAtStartup ? "1" : "0",
            ["launch_screenshot_tool_with_app"] = settings.LaunchScreenshotToolWithApp ? "1" : "0",
            ["screenshot_tool_hotkey"] = settings.ScreenshotToolHotkey,
            ["screenshot_tool_hotkey_migration_version"] = settings.ScreenshotToolHotkeyMigrationVersion.ToString(CultureInfo.InvariantCulture),
            ["screenshot_tool_save_directory"] = settings.ScreenshotToolSaveDirectory,
            ["issue_report_directory"] = settings.IssueReportDirectory,
            ["enable_cloud_sync"] = settings.EnableCloudSync ? "1" : "0",
            ["allow_raw_conversation_cloud_sync"] = settings.AllowRawConversationCloudSync ? "1" : "0",
            ["allow_automatic_issue_report_upload"] = settings.AllowAutomaticIssueReportUpload ? "1" : "0",
            ["enable_update_checks"] = settings.EnableUpdateChecks ? "1" : "0",
            ["server_base_url"] = settings.ServerBaseUrl,
            ["enable_ai_drafts"] = settings.EnableAiDrafts ? "1" : "0",
            ["ollama_base_url"] = settings.OllamaBaseUrl,
            ["ollama_model_name"] = settings.OllamaModelName,
            ["ai_draft_count"] = settings.AiDraftCount.ToString(CultureInfo.InvariantCulture),
            ["ai_request_timeout_ms"] = settings.AiRequestTimeoutMs.ToString(CultureInfo.InvariantCulture),
            ["enable_ai_api_fallback"] = settings.EnableAiApiFallback ? "1" : "0",
            ["ai_api_base_url"] = settings.AiApiBaseUrl,
            ["ai_api_model_name"] = settings.AiApiModelName,
            [AiApiKeySettingKey] = string.Empty,
            ["allow_remote_ai_for_customer_text"] = settings.AllowRemoteAiForCustomerText ? "1" : "0",
            ["sync_client_id"] = settings.SyncClientId,
            [SyncApiTokenSettingKey] = string.Empty,
            ["last_knowledge_sync_at"] = settings.LastKnowledgeSyncAt.HasValue ? ToDbDateTime(settings.LastKnowledgeSyncAt.Value) : string.Empty,
            ["last_conversation_sync_at"] = settings.LastConversationSyncAt.HasValue ? ToDbDateTime(settings.LastConversationSyncAt.Value) : string.Empty,
            ["last_published_knowledge_sync_at"] = settings.LastPublishedKnowledgeSyncAt.HasValue ? ToDbDateTime(settings.LastPublishedKnowledgeSyncAt.Value) : string.Empty,
            ["last_published_knowledge_revision"] = settings.LastPublishedKnowledgeRevision.ToString(CultureInfo.InvariantCulture),
            ["last_update_check_at"] = settings.LastUpdateCheckAt.HasValue ? ToDbDateTime(settings.LastUpdateCheckAt.Value) : string.Empty,
            ["last_known_cloud_knowledge_version"] = settings.LastKnownCloudKnowledgeVersion,
            ["last_published_knowledge_version"] = settings.LastPublishedKnowledgeVersion,
            ["run_as_administrator"] = settings.RunAsAdministrator ? "1" : "0",
            ["has_completed_first_run_guide"] = settings.HasCompletedFirstRunGuide ? "1" : "0",
            ["phrase_image_send_mode"] = settings.PhraseImageSendMode.ToString(),
            ["default_search_overlay_mode"] = settings.DefaultSearchOverlayMode.ToString(),
            ["recommendation_display_count"] = settings.RecommendationDisplayCount.ToString(CultureInfo.InvariantCulture),
            ["search_overlay_width_percent"] = settings.SearchOverlayWidthPercent.ToString(CultureInfo.InvariantCulture),
            ["search_overlay_height_percent"] = settings.SearchOverlayHeightPercent.ToString(CultureInfo.InvariantCulture),
            ["search_input_width_percent"] = settings.SearchInputWidthPercent.ToString(CultureInfo.InvariantCulture),
            ["search_input_box_height"] = settings.SearchInputBoxHeight.ToString(CultureInfo.InvariantCulture),
            ["sidebar_width"] = settings.SidebarWidth.ToString(CultureInfo.InvariantCulture),
            ["sidebar_height_percent"] = settings.SidebarHeightPercent.ToString(CultureInfo.InvariantCulture),
            ["category_tabs_per_row"] = settings.CategoryTabsPerRow.ToString(CultureInfo.InvariantCulture),
            ["category_font_size"] = settings.CategoryFontSize.ToString(CultureInfo.InvariantCulture),
            ["phrase_font_size"] = settings.PhraseFontSize.ToString(CultureInfo.InvariantCulture),
            ["phrase_row_spacing"] = settings.PhraseRowSpacing.ToString(CultureInfo.InvariantCulture),
            ["phrase_list_density"] = settings.PhraseListDensity.ToString(),
            ["file_library_folder_path"] = settings.FileLibraryFolderPath,
            ["phrase_image_folder_path"] = settings.PhraseImageFolderPath,
            ["restore_focus_delay_ms"] = settings.RestoreFocusDelayMs.ToString(CultureInfo.InvariantCulture),
            ["activation_retry_delay_ms"] = settings.ActivationRetryDelayMs.ToString(CultureInfo.InvariantCulture),
            ["send_delay_ms"] = settings.SendDelayMs.ToString(CultureInfo.InvariantCulture),
            ["clipboard_restore_delay_ms"] = settings.ClipboardRestoreDelayMs.ToString(CultureInfo.InvariantCulture),
            ["text_paste_delay_ms"] = settings.TextPasteDelayMs.ToString(CultureInfo.InvariantCulture),
            ["image_paste_delay_ms"] = settings.ImagePasteDelayMs.ToString(CultureInfo.InvariantCulture),
            ["rich_image_send_delay_ms"] = settings.RichImageSendDelayMs.ToString(CultureInfo.InvariantCulture),
            ["rich_image_clipboard_restore_delay_ms"] = settings.RichImageClipboardRestoreDelayMs.ToString(CultureInfo.InvariantCulture),
            ["phrase_hover_preview_delay_ms"] = settings.PhraseHoverPreviewDelayMs.ToString(CultureInfo.InvariantCulture),
            ["log_screenshot_retention_days"] = settings.LogScreenshotRetentionDays.ToString(CultureInfo.InvariantCulture),
            ["log_screenshot_max_size_mb"] = settings.LogScreenshotMaxSizeMb.ToString(CultureInfo.InvariantCulture),
            ["log_text_retention_days"] = settings.LogTextRetentionDays.ToString(CultureInfo.InvariantCulture),
            ["category_color_hex"] = settings.CategoryColorHex,
            ["category_opacity_percent"] = settings.CategoryOpacityPercent.ToString(CultureInfo.InvariantCulture),
            ["tag_color_hex"] = settings.TagColorHex,
            ["tag_opacity_percent"] = settings.TagOpacityPercent.ToString(CultureInfo.InvariantCulture),
            ["phrase_color_hex"] = settings.PhraseColorHex,
            ["phrase_opacity_percent"] = settings.PhraseOpacityPercent.ToString(CultureInfo.InvariantCulture),
            ["sidebar_color_hex"] = settings.SidebarColorHex,
            ["sidebar_opacity_percent"] = settings.SidebarOpacityPercent.ToString(CultureInfo.InvariantCulture),
            ["search_overlay_color_hex"] = settings.SearchOverlayColorHex,
            ["search_overlay_opacity_percent"] = settings.SearchOverlayOpacityPercent.ToString(CultureInfo.InvariantCulture),
            ["search_input_color_hex"] = settings.SearchInputColorHex,
            ["search_input_opacity_percent"] = settings.SearchInputOpacityPercent.ToString(CultureInfo.InvariantCulture)
        };

        await using var connection = await OpenConnectionAsync();
        await InitializeSchemaAsync(connection);
        using var transaction = connection.BeginTransaction();

        foreach (var pair in settingsMap)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO app_settings (key, value)
                VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$key", pair.Key);
            command.Parameters.AddWithValue("$value", pair.Value);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        await _secretStore.SaveSecretsAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AiApiKeySettingKey] = settings.AiApiKey,
            [SyncApiTokenSettingKey] = settings.SyncApiToken
        }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AppearanceThemePreset>> LoadAppearanceThemesAsync()
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_settings WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", AppearanceThemePresetsKey);

        var rawJson = (string?)await command.ExecuteScalarAsync();
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<AppearanceThemePreset>>(rawJson) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task SaveAppearanceThemesAsync(IReadOnlyCollection<AppearanceThemePreset> themes)
    {
        var json = JsonSerializer.Serialize(themes);

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_settings (key, value)
            VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", AppearanceThemePresetsKey);
        command.Parameters.AddWithValue("$value", json);
        await command.ExecuteNonQueryAsync();
    }

    public async Task ResetPhraseDataToDefaultsAsync()
    {
        await using var connection = await OpenConnectionAsync();
        using var transaction = connection.BeginTransaction();

        foreach (var sql in new[]
                 {
                     "DELETE FROM quick_tags;",
                     "DELETE FROM phrase_usage_stats;",
                     "DELETE FROM phrases;",
                     "DELETE FROM categories;"
                 })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        await SeedIfNeededAsync(connection);
    }

    public async Task ClearFileLibraryConfigurationAsync()
    {
        await using var connection = await OpenConnectionAsync();
        using var transaction = connection.BeginTransaction();

        foreach (var sql in new[]
                 {
                     "DELETE FROM file_usage_stats;",
                     "DELETE FROM file_items;"
                 })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        await using (var settingCommand = connection.CreateCommand())
        {
            settingCommand.Transaction = transaction;
            settingCommand.CommandText = """
                INSERT INTO app_settings (key, value)
                VALUES ('file_library_folder_path', '')
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            await settingCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task RecordAppLaunchAsync()
    {
        await IncrementAppSettingAsync("launch_count", 1);
    }

    public async Task<long> GetLaunchCountAsync()
    {
        await using var connection = await OpenConnectionAsync();
        return await GetNumericAppSettingAsync(connection, "launch_count");
    }

    public async Task AddRuntimeSecondsAsync(long runtimeSeconds)
    {
        if (runtimeSeconds <= 0)
        {
            return;
        }

        await IncrementAppSettingAsync("total_runtime_seconds", runtimeSeconds);
    }

    public async Task RecordPhraseSendAsync(Guid phraseId, DateTimeOffset sentAt)
    {
        const string sql = """
            INSERT INTO phrase_usage_stats (phrase_id, send_count, last_sent_at)
            VALUES ($phraseId, 1, $lastSentAt)
            ON CONFLICT(phrase_id) DO UPDATE SET
                send_count = phrase_usage_stats.send_count + 1,
                last_sent_at = excluded.last_sent_at;
            """;

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$phraseId", phraseId.ToString());
        command.Parameters.AddWithValue("$lastSentAt", ToDbDateTime(sentAt));
        await command.ExecuteNonQueryAsync();
    }

    public async Task<Dictionary<Guid, PhraseUsageStat>> LoadPhraseUsageStatsAsync()
    {
        const string sql = """
            SELECT phrase_id, send_count, last_sent_at
            FROM phrase_usage_stats;
            """;

        var stats = new Dictionary<Guid, PhraseUsageStat>();

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var phraseId = Guid.Parse(reader.GetString(0));
            stats[phraseId] = new PhraseUsageStat
            {
                PhraseId = phraseId,
                SendCount = reader.GetInt32(1),
                LastSentAt = reader.IsDBNull(2) ? null : ParseDbDateTime(reader.GetString(2))
            };
        }

        return stats;
    }

    public async Task RecordFileUseAsync(Guid fileItemId, DateTimeOffset usedAt)
    {
        const string sql = """
            INSERT INTO file_usage_stats (file_item_id, use_count, last_used_at)
            VALUES ($fileItemId, 1, $lastUsedAt)
            ON CONFLICT(file_item_id) DO UPDATE SET
                use_count = file_usage_stats.use_count + 1,
                last_used_at = excluded.last_used_at;
            """;

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$fileItemId", fileItemId.ToString());
        command.Parameters.AddWithValue("$lastUsedAt", ToDbDateTime(usedAt));
        await command.ExecuteNonQueryAsync();
    }

    public async Task<Dictionary<Guid, FileUsageStat>> LoadFileUsageStatsAsync()
    {
        const string sql = """
            SELECT file_item_id, use_count, last_used_at
            FROM file_usage_stats;
            """;

        var stats = new Dictionary<Guid, FileUsageStat>();

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var fileItemId = Guid.Parse(reader.GetString(0));
            stats[fileItemId] = new FileUsageStat
            {
                FileItemId = fileItemId,
                UseCount = reader.GetInt32(1),
                LastUsedAt = reader.IsDBNull(2) ? null : ParseDbDateTime(reader.GetString(2))
            };
        }

        return stats;
    }

    public async Task<UsageDashboardStats> GetUsageDashboardStatsAsync(int topCount = 5, int lowUseCount = 5)
    {
        await using var connection = await OpenConnectionAsync();

        var launchCount = await GetNumericAppSettingAsync(connection, "launch_count");
        var totalRuntimeSeconds = await GetNumericAppSettingAsync(connection, "total_runtime_seconds");

        var mostUsedPhrases = await LoadDashboardPhrasesAsync(
            connection,
            """
            SELECT p.id, c.name, p.title, p.content, COALESCE(u.send_count, 0), u.last_sent_at
            FROM phrases p
            INNER JOIN categories c ON c.id = p.category_id
            LEFT JOIN phrase_usage_stats u ON u.phrase_id = p.id
            ORDER BY COALESCE(u.send_count, 0) DESC,
                     COALESCE(u.last_sent_at, '') DESC,
                     p.updated_at DESC
            LIMIT $limit;
            """,
            topCount);

        var leastUsedPhrases = await LoadDashboardPhrasesAsync(
            connection,
            """
            SELECT p.id, c.name, p.title, p.content, COALESCE(u.send_count, 0), u.last_sent_at
            FROM phrases p
            INNER JOIN categories c ON c.id = p.category_id
            LEFT JOIN phrase_usage_stats u ON u.phrase_id = p.id
            ORDER BY COALESCE(u.send_count, 0) ASC,
                     CASE WHEN u.last_sent_at IS NULL THEN 0 ELSE 1 END ASC,
                     COALESCE(u.last_sent_at, '') ASC,
                     p.updated_at ASC
            LIMIT $limit;
            """,
            lowUseCount);

        return new UsageDashboardStats
        {
            LaunchCount = (int)launchCount,
            TotalRuntimeSeconds = totalRuntimeSeconds,
            MostUsedPhrases = mostUsedPhrases,
            LeastUsedPhrases = leastUsedPhrases
        };
    }

    public async Task<IReadOnlyList<PhraseUsageReportItem>> GetPhraseUsageReportItemsAsync()
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, c.name, p.title, p.content, COALESCE(u.send_count, 0), u.last_sent_at
            FROM phrases p
            INNER JOIN categories c ON c.id = p.category_id
            LEFT JOIN phrase_usage_stats u ON u.phrase_id = p.id
            ORDER BY COALESCE(u.send_count, 0) DESC,
                     COALESCE(u.last_sent_at, '') DESC,
                     p.updated_at DESC;
            """;

        var items = new List<PhraseUsageReportItem>();
        await using var reader = await command.ExecuteReaderAsync();
        var rank = 1;
        while (await reader.ReadAsync())
        {
            items.Add(new PhraseUsageReportItem
            {
                Rank = rank++,
                PhraseId = Guid.Parse(reader.GetString(0)),
                CategoryName = reader.GetString(1),
                Title = reader.GetString(2),
                ContentPreview = reader.GetString(3),
                SendCount = reader.GetInt32(4),
                LastSentAt = reader.IsDBNull(5) ? null : ParseDbDateTime(reader.GetString(5))
            });
        }

        return items;
    }

    private async Task IncrementAppSettingAsync(string key, long delta)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_settings (key, value)
            VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET
                value = CAST(COALESCE(app_settings.value, '0') AS INTEGER) + $delta;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", delta.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$delta", delta);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> GetNumericAppSettingAsync(SqliteConnection connection, string key)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_settings WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);

        var value = await command.ExecuteScalarAsync();
        return value is string stringValue &&
               long.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericValue)
            ? numericValue
            : 0L;
    }

    private static async Task<IReadOnlyList<PhraseUsageDashboardItem>> LoadDashboardPhrasesAsync(
        SqliteConnection connection,
        string sql,
        int limit)
    {
        var items = new List<PhraseUsageDashboardItem>();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$limit", limit);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new PhraseUsageDashboardItem
            {
                PhraseId = Guid.Parse(reader.GetString(0)),
                CategoryName = reader.GetString(1),
                Title = reader.GetString(2),
                ContentPreview = reader.GetString(3),
                SendCount = reader.GetInt32(4),
                LastSentAt = reader.IsDBNull(5) ? null : ParseDbDateTime(reader.GetString(5))
            });
        }

        return items;
    }

    private async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        await pragma.ExecuteNonQueryAsync();

        return connection;
    }

    private static async Task InitializeSchemaAsync(SqliteConnection connection)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS categories (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                sort_order INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS phrases (
                id TEXT PRIMARY KEY,
                category_id TEXT NOT NULL,
                title TEXT NOT NULL,
                tags TEXT NOT NULL DEFAULT '',
                content TEXT NOT NULL DEFAULT '',
                image_relative_path TEXT NULL,
                sort_order INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY (category_id) REFERENCES categories(id) ON DELETE RESTRICT
            );

            CREATE INDEX IF NOT EXISTS idx_phrases_category_id
            ON phrases(category_id);

            CREATE TABLE IF NOT EXISTS quick_tags (
                id TEXT PRIMARY KEY,
                phrase_id TEXT NOT NULL UNIQUE,
                sort_order INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                FOREIGN KEY (phrase_id) REFERENCES phrases(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_quick_tags_sort_order
            ON quick_tags(sort_order);

            CREATE TABLE IF NOT EXISTS file_items (
                id TEXT PRIMARY KEY,
                display_name TEXT NOT NULL,
                full_path TEXT NOT NULL UNIQUE,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_file_items_display_name
            ON file_items(display_name);

            CREATE TABLE IF NOT EXISTS knowledge_cards (
                id TEXT PRIMARY KEY,
                candidate_kind TEXT NOT NULL DEFAULT 'qa',
                platform TEXT NOT NULL DEFAULT '',
                product_key TEXT NOT NULL DEFAULT '',
                question TEXT NOT NULL DEFAULT '',
                answer TEXT NOT NULL DEFAULT '',
                synonyms_json TEXT NOT NULL DEFAULT '[]',
                tags_json TEXT NOT NULL DEFAULT '[]',
                usage_notes TEXT NOT NULL DEFAULT '',
                version TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_knowledge_cards_platform
            ON knowledge_cards(platform, updated_at DESC);

            CREATE INDEX IF NOT EXISTS idx_knowledge_cards_product_key
            ON knowledge_cards(product_key, updated_at DESC);

            CREATE TABLE IF NOT EXISTS phrase_usage_stats (
                phrase_id TEXT PRIMARY KEY,
                send_count INTEGER NOT NULL DEFAULT 0,
                last_sent_at TEXT NULL,
                FOREIGN KEY (phrase_id) REFERENCES phrases(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_phrase_usage_stats_send_count
            ON phrase_usage_stats(send_count DESC, last_sent_at DESC);

            CREATE TABLE IF NOT EXISTS file_usage_stats (
                file_item_id TEXT PRIMARY KEY,
                use_count INTEGER NOT NULL DEFAULT 0,
                last_used_at TEXT NULL,
                FOREIGN KEY (file_item_id) REFERENCES file_items(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_file_usage_stats_use_count
            ON file_usage_stats(use_count DESC, last_used_at DESC);

            CREATE TABLE IF NOT EXISTS app_settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
        await EnsureColumnAsync(connection, "phrases", "image_relative_path", "TEXT NULL");
        await EnsureColumnAsync(connection, "knowledge_cards", "candidate_kind", "TEXT NOT NULL DEFAULT 'qa'");
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string tableName, string columnName, string columnDefinition)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await reader.DisposeAsync();

        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        await alterCommand.ExecuteNonQueryAsync();
    }

    private static async Task SeedIfNeededAsync(SqliteConnection connection)
    {
        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(1) FROM categories;";

        var count = (long)(await countCommand.ExecuteScalarAsync() ?? 0L);
        if (count > 0)
        {
            return;
        }

        var now = DateTimeOffset.Now;

        var categories = new[]
        {
            new ReplyCategory
            {
                Id = Guid.NewGuid(),
                Name = "订单咨询",
                SortOrder = 1,
                CreatedAt = now,
                UpdatedAt = now
            },
            new ReplyCategory
            {
                Id = Guid.NewGuid(),
                Name = "物流发货",
                SortOrder = 2,
                CreatedAt = now,
                UpdatedAt = now
            },
            new ReplyCategory
            {
                Id = Guid.NewGuid(),
                Name = "售后处理",
                SortOrder = 3,
                CreatedAt = now,
                UpdatedAt = now
            }
        };

        var phrases = new[]
        {
            new ReplyPhrase
            {
                Id = Guid.NewGuid(),
                CategoryId = categories[0].Id,
                CategoryName = categories[0].Name,
                Title = "开场欢迎",
                Tags = "欢迎,开场",
                Content = "亲，欢迎光临，有任何商品、优惠或发货问题都可以直接告诉我，我这边马上帮您处理。",
                SortOrder = 1,
                CreatedAt = now,
                UpdatedAt = now
            },
            new ReplyPhrase
            {
                Id = Guid.NewGuid(),
                CategoryId = categories[0].Id,
                CategoryName = categories[0].Name,
                Title = "催付提醒",
                Tags = "催付,订单",
                Content = "亲，您看的这款商品库存比较紧张，如果确定要的话可以尽快付款，我这边帮您持续关注。",
                SortOrder = 2,
                CreatedAt = now,
                UpdatedAt = now
            },
            new ReplyPhrase
            {
                Id = Guid.NewGuid(),
                CategoryId = categories[1].Id,
                CategoryName = categories[1].Name,
                Title = "发货时效",
                Tags = "发货,物流",
                Content = "您好，这边会在 24 小时内为您安排发出，发出后您可以在订单详情里查看物流更新。",
                SortOrder = 1,
                CreatedAt = now,
                UpdatedAt = now
            },
            new ReplyPhrase
            {
                Id = Guid.NewGuid(),
                CategoryId = categories[1].Id,
                CategoryName = categories[1].Name,
                Title = "物流安抚",
                Tags = "物流,安抚",
                Content = "亲，这边已经帮您跟进物流状态了，物流途中会有一点更新延迟，我会继续帮您关注，有新进展第一时间告诉您。",
                SortOrder = 2,
                CreatedAt = now,
                UpdatedAt = now
            },
            new ReplyPhrase
            {
                Id = Guid.NewGuid(),
                CategoryId = categories[2].Id,
                CategoryName = categories[2].Name,
                Title = "收集信息",
                Tags = "售后,登记",
                Content = "辛苦您把订单号、问题照片和具体情况发我，我先帮您登记核实，再尽快给您处理方案。",
                SortOrder = 1,
                CreatedAt = now,
                UpdatedAt = now
            }
        };

        using var transaction = connection.BeginTransaction();

        foreach (var category in categories)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO categories (id, name, sort_order, created_at, updated_at)
                VALUES ($id, $name, $sortOrder, $createdAt, $updatedAt);
                """;
            command.Parameters.AddWithValue("$id", category.Id.ToString());
            command.Parameters.AddWithValue("$name", category.Name);
            command.Parameters.AddWithValue("$sortOrder", category.SortOrder);
            command.Parameters.AddWithValue("$createdAt", ToDbDateTime(category.CreatedAt));
            command.Parameters.AddWithValue("$updatedAt", ToDbDateTime(category.UpdatedAt));
            await command.ExecuteNonQueryAsync();
        }

        foreach (var phrase in phrases)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO phrases (id, category_id, title, tags, content, image_relative_path, sort_order, created_at, updated_at)
                VALUES ($id, $categoryId, $title, $tags, $content, $imageRelativePath, $sortOrder, $createdAt, $updatedAt);
                """;
            command.Parameters.AddWithValue("$id", phrase.Id.ToString());
            command.Parameters.AddWithValue("$categoryId", phrase.CategoryId.ToString());
            command.Parameters.AddWithValue("$title", phrase.Title);
            command.Parameters.AddWithValue("$tags", phrase.Tags);
            command.Parameters.AddWithValue("$content", phrase.Content);
            command.Parameters.AddWithValue("$imageRelativePath", phrase.ImageRelativePath);
            command.Parameters.AddWithValue("$sortOrder", phrase.SortOrder);
            command.Parameters.AddWithValue("$createdAt", ToDbDateTime(phrase.CreatedAt));
            command.Parameters.AddWithValue("$updatedAt", ToDbDateTime(phrase.UpdatedAt));
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task<List<ReplyCategory>> LoadCategoriesAsync(SqliteConnection connection)
    {
        var categories = new List<ReplyCategory>();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, sort_order, created_at, updated_at
            FROM categories
            ORDER BY sort_order, created_at;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            categories.Add(new ReplyCategory
            {
                Id = Guid.Parse(reader.GetString(0)),
                Name = reader.GetString(1),
                SortOrder = reader.GetInt32(2),
                CreatedAt = ParseDbDateTime(reader.GetString(3)),
                UpdatedAt = ParseDbDateTime(reader.GetString(4))
            });
        }

        return categories;
    }

    private static async Task<List<ReplyPhrase>> LoadPhrasesAsync(SqliteConnection connection)
    {
        var phrases = new List<ReplyPhrase>();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, p.category_id, c.name, p.title, p.tags, p.content, IFNULL(p.image_relative_path, ''), p.sort_order, p.created_at, p.updated_at
            FROM phrases p
            INNER JOIN categories c ON c.id = p.category_id
            ORDER BY p.sort_order, p.created_at;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            phrases.Add(new ReplyPhrase
            {
                Id = Guid.Parse(reader.GetString(0)),
                CategoryId = Guid.Parse(reader.GetString(1)),
                CategoryName = reader.GetString(2),
                Title = reader.GetString(3),
                Tags = reader.GetString(4),
                Content = reader.GetString(5),
                ImageRelativePath = reader.GetString(6),
                SortOrder = reader.GetInt32(7),
                CreatedAt = ParseDbDateTime(reader.GetString(8)),
                UpdatedAt = ParseDbDateTime(reader.GetString(9))
            });
        }

        return phrases;
    }

    private static async Task<List<QuickTag>> LoadQuickTagsAsync(SqliteConnection connection)
    {
        var quickTags = new List<QuickTag>();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT qt.id, qt.phrase_id, p.title, p.content, c.name, qt.sort_order, qt.created_at
            FROM quick_tags qt
            INNER JOIN phrases p ON p.id = qt.phrase_id
            INNER JOIN categories c ON c.id = p.category_id
            ORDER BY qt.sort_order, qt.created_at;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            quickTags.Add(new QuickTag
            {
                Id = Guid.Parse(reader.GetString(0)),
                PhraseId = Guid.Parse(reader.GetString(1)),
                PhraseTitle = reader.GetString(2),
                PhraseContent = reader.GetString(3),
                PhraseCategoryName = reader.GetString(4),
                SortOrder = reader.GetInt32(5),
                CreatedAt = ParseDbDateTime(reader.GetString(6))
            });
        }

        return quickTags;
    }

    private static async Task<List<LibraryFileItem>> LoadFileItemsAsync(SqliteConnection connection)
    {
        var fileItems = new List<LibraryFileItem>();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, display_name, full_path, created_at, updated_at
            FROM file_items
            ORDER BY display_name, created_at;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            fileItems.Add(new LibraryFileItem
            {
                Id = Guid.Parse(reader.GetString(0)),
                DisplayName = reader.GetString(1),
                FullPath = reader.GetString(2),
                CreatedAt = ParseDbDateTime(reader.GetString(3)),
                UpdatedAt = ParseDbDateTime(reader.GetString(4))
            });
        }

        return fileItems;
    }

    private static async Task<IReadOnlyList<PublishedKnowledgeCard>> LoadPublishedKnowledgeCardsAsync(SqliteConnection connection)
    {
        var cards = new List<PublishedKnowledgeCard>();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,
                   candidate_kind,
                   platform,
                   product_key,
                   question,
                   answer,
                   synonyms_json,
                   tags_json,
                   usage_notes,
                   version,
                   updated_at
            FROM knowledge_cards
            ORDER BY updated_at DESC, question ASC;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            cards.Add(new PublishedKnowledgeCard
            {
                Id = Guid.Parse(reader.GetString(0)),
                CandidateKind = reader.GetString(1),
                Platform = reader.GetString(2),
                ProductKey = reader.GetString(3),
                Question = reader.GetString(4),
                Answer = reader.GetString(5),
                Synonyms = DeserializeStringList(reader.GetString(6)),
                Tags = DeserializeStringList(reader.GetString(7)),
                UsageNotes = reader.GetString(8),
                Version = reader.GetString(9),
                UpdatedAt = ParseDbDateTime(reader.GetString(10))
            });
        }

        return cards;
    }

    private async Task<AppSettings> LoadSettingsAsync(SqliteConnection connection)
    {
        var settings = new AppSettings();
        settings.ScreenshotToolHotkeyMigrationVersion = 0;
        var legacyAiApiKey = string.Empty;
        var legacySyncApiToken = string.Empty;

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM app_settings;";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var key = reader.GetString(0);
            var value = reader.GetString(1);

            switch (key)
            {
                case "send_hotkey_mode":
                    if (Enum.TryParse<SendHotkeyMode>(value, true, out var sendHotkeyMode))
                    {
                        settings.SendHotkeyMode = sendHotkeyMode;
                    }
                    break;
                case "search_focus_hotkey_mode":
                    if (Enum.TryParse<SearchFocusHotkeyMode>(value, true, out var searchFocusHotkeyMode))
                    {
                        settings.SearchFocusHotkeyMode = searchFocusHotkeyMode;
                    }
                    break;
                case "sidebar_work_mode":
                    if (Enum.TryParse<SidebarWorkMode>(value, true, out var sidebarWorkMode))
                    {
                        settings.SidebarWorkMode = sidebarWorkMode;
                    }
                    break;
                case "launch_at_startup":
                    settings.LaunchAtStartup = value is "1" or "true" or "True";
                    break;
                case "launch_screenshot_tool_with_app":
                    settings.LaunchScreenshotToolWithApp = value is "1" or "true" or "True";
                    break;
                case "screenshot_tool_hotkey":
                    settings.ScreenshotToolHotkey = value;
                    break;
                case "screenshot_tool_hotkey_migration_version":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hotkeyMigrationVersion))
                    {
                        settings.ScreenshotToolHotkeyMigrationVersion = hotkeyMigrationVersion;
                    }
                    break;
                case "screenshot_tool_save_directory":
                    settings.ScreenshotToolSaveDirectory = value;
                    break;
                case "issue_report_directory":
                    settings.IssueReportDirectory = value;
                    break;
                case "enable_cloud_sync":
                    settings.EnableCloudSync = value is "1" or "true" or "True";
                    break;
                case "allow_raw_conversation_cloud_sync":
                    settings.AllowRawConversationCloudSync = value is "1" or "true" or "True";
                    break;
                case "allow_automatic_issue_report_upload":
                    settings.AllowAutomaticIssueReportUpload = value is "1" or "true" or "True";
                    break;
                case "enable_update_checks":
                    settings.EnableUpdateChecks = value is "1" or "true" or "True";
                    break;
                case "server_base_url":
                    settings.ServerBaseUrl = value;
                    break;
                case "enable_ai_drafts":
                    settings.EnableAiDrafts = value is "1" or "true" or "True";
                    break;
                case "ollama_base_url":
                    settings.OllamaBaseUrl = value;
                    break;
                case "ollama_model_name":
                    settings.OllamaModelName = value;
                    break;
                case "ai_draft_count":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var aiDraftCount))
                    {
                        settings.AiDraftCount = aiDraftCount;
                    }
                    break;
                case "ai_request_timeout_ms":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var aiRequestTimeoutMs))
                    {
                        settings.AiRequestTimeoutMs = aiRequestTimeoutMs;
                    }
                    break;
                case "enable_ai_api_fallback":
                    settings.EnableAiApiFallback = value is "1" or "true" or "True";
                    break;
                case "ai_api_base_url":
                    settings.AiApiBaseUrl = value;
                    break;
                case "ai_api_model_name":
                    settings.AiApiModelName = value;
                    break;
                case "ai_api_key":
                    legacyAiApiKey = value;
                    break;
                case "allow_remote_ai_for_customer_text":
                    settings.AllowRemoteAiForCustomerText = value is "1" or "true" or "True";
                    break;
                case "sync_client_id":
                    settings.SyncClientId = value;
                    break;
                case "sync_api_token":
                    legacySyncApiToken = value;
                    break;
                case "last_knowledge_sync_at":
                    settings.LastKnowledgeSyncAt = string.IsNullOrWhiteSpace(value) ? null : ParseDbDateTime(value);
                    break;
                case "last_conversation_sync_at":
                    settings.LastConversationSyncAt = string.IsNullOrWhiteSpace(value) ? null : ParseDbDateTime(value);
                    break;
                case "last_published_knowledge_sync_at":
                    settings.LastPublishedKnowledgeSyncAt = string.IsNullOrWhiteSpace(value) ? null : ParseDbDateTime(value);
                    break;
                case "last_published_knowledge_revision":
                    settings.LastPublishedKnowledgeRevision = ParseInt64(value, 0, 0, long.MaxValue);
                    break;
                case "last_update_check_at":
                    settings.LastUpdateCheckAt = string.IsNullOrWhiteSpace(value) ? null : ParseDbDateTime(value);
                    break;
                case "last_known_cloud_knowledge_version":
                    settings.LastKnownCloudKnowledgeVersion = value;
                    break;
                case "last_published_knowledge_version":
                    settings.LastPublishedKnowledgeVersion = value;
                    break;
                case "run_as_administrator":
                    settings.RunAsAdministrator = value is "1" or "true" or "True";
                    break;
                case "has_completed_first_run_guide":
                    settings.HasCompletedFirstRunGuide = value is "1" or "true" or "True";
                    break;
                case "phrase_image_send_mode":
                    if (Enum.TryParse<PhraseImageSendMode>(value, true, out var phraseImageSendMode))
                    {
                        settings.PhraseImageSendMode = phraseImageSendMode;
                    }
                    break;
                case "default_search_overlay_mode":
                    if (Enum.TryParse<SearchOverlayMode>(value, true, out var defaultSearchOverlayMode))
                    {
                        settings.DefaultSearchOverlayMode = defaultSearchOverlayMode;
                    }
                    break;
                case "recommendation_display_count":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var recommendationDisplayCount))
                    {
                        settings.RecommendationDisplayCount = recommendationDisplayCount;
                    }
                    break;
                case "search_overlay_width_percent":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var searchOverlayWidthPercent))
                    {
                        settings.SearchOverlayWidthPercent = searchOverlayWidthPercent;
                    }
                    break;
                case "search_overlay_height_percent":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var searchOverlayHeightPercent))
                    {
                        settings.SearchOverlayHeightPercent = searchOverlayHeightPercent;
                    }
                    break;
                case "search_input_width_percent":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var searchInputWidthPercent))
                    {
                        settings.SearchInputWidthPercent = searchInputWidthPercent;
                    }
                    break;
                case "search_input_box_height":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var searchInputBoxHeight))
                    {
                        settings.SearchInputBoxHeight = searchInputBoxHeight;
                    }
                    break;
                case "search_input_height":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var searchInputHeight))
                    {
                        settings.SearchOverlayHeightPercent = searchInputHeight;
                    }
                    break;
                case "sidebar_width":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sidebarWidth))
                    {
                        settings.SidebarWidth = sidebarWidth;
                    }
                    break;
                case "sidebar_width_percent":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var legacySidebarWidthPercent))
                    {
                        settings.SidebarWidth = legacySidebarWidthPercent switch
                        {
                            <= 20 => 220,
                            <= 24 => 260,
                            <= 28 => 300,
                            <= 32 => 340,
                            <= 36 => 380,
                            <= 40 => 420,
                            _ => 460
                        };
                    }
                    break;
                case "sidebar_height_percent":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sidebarHeightPercent))
                    {
                        settings.SidebarHeightPercent = sidebarHeightPercent;
                    }
                    break;
                case "category_tabs_per_row":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var categoryTabsPerRow))
                    {
                        settings.CategoryTabsPerRow = categoryTabsPerRow;
                    }
                    break;
                case "category_font_size":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var categoryFontSize))
                    {
                        settings.CategoryFontSize = categoryFontSize;
                    }
                    break;
                case "phrase_font_size":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var phraseFontSize))
                    {
                        settings.PhraseFontSize = phraseFontSize;
                    }
                    break;
                case "phrase_row_spacing":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var phraseRowSpacing))
                    {
                        settings.PhraseRowSpacing = phraseRowSpacing;
                    }
                    break;
                case "phrase_list_density":
                    if (Enum.TryParse<PhraseListDensity>(value, true, out var phraseListDensity))
                    {
                        settings.PhraseListDensity = phraseListDensity;
                    }
                    break;
                case "file_library_folder_path":
                    settings.FileLibraryFolderPath = value;
                    break;
                case "phrase_image_folder_path":
                    settings.PhraseImageFolderPath = value;
                    break;
                case "restore_focus_delay_ms":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var restoreFocusDelayMs))
                    {
                        settings.RestoreFocusDelayMs = restoreFocusDelayMs;
                    }
                    break;
                case "activation_retry_delay_ms":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var activationRetryDelayMs))
                    {
                        settings.ActivationRetryDelayMs = activationRetryDelayMs;
                    }
                    break;
                case "send_delay_ms":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sendDelayMs))
                    {
                        settings.SendDelayMs = sendDelayMs;
                    }
                    break;
                case "clipboard_restore_delay_ms":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var clipboardRestoreDelayMs))
                    {
                        settings.ClipboardRestoreDelayMs = clipboardRestoreDelayMs;
                    }
                    break;
                case "text_paste_delay_ms":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var textPasteDelayMs))
                    {
                        settings.TextPasteDelayMs = textPasteDelayMs;
                    }
                    break;
                case "image_paste_delay_ms":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var imagePasteDelayMs))
                    {
                        settings.ImagePasteDelayMs = imagePasteDelayMs;
                    }
                    break;
                case "rich_image_send_delay_ms":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var richImageSendDelayMs))
                    {
                        settings.RichImageSendDelayMs = richImageSendDelayMs;
                    }
                    break;
                case "rich_image_clipboard_restore_delay_ms":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var richImageClipboardRestoreDelayMs))
                    {
                        settings.RichImageClipboardRestoreDelayMs = richImageClipboardRestoreDelayMs;
                    }
                    break;
                case "phrase_hover_preview_delay_ms":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var phraseHoverPreviewDelayMs))
                    {
                        settings.PhraseHoverPreviewDelayMs = phraseHoverPreviewDelayMs;
                    }
                    break;
                case "log_screenshot_retention_days":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var logScreenshotRetentionDays))
                    {
                        settings.LogScreenshotRetentionDays = logScreenshotRetentionDays;
                    }
                    break;
                case "log_screenshot_max_size_mb":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var logScreenshotMaxSizeMb))
                    {
                        settings.LogScreenshotMaxSizeMb = logScreenshotMaxSizeMb;
                    }
                    break;
                case "log_text_retention_days":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var logTextRetentionDays))
                    {
                        settings.LogTextRetentionDays = logTextRetentionDays;
                    }
                    break;
                case "category_color_hex":
                    settings.CategoryColorHex = value;
                    break;
                case "category_opacity_percent":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var categoryOpacityPercent))
                    {
                        settings.CategoryOpacityPercent = categoryOpacityPercent;
                    }
                    break;
                case "tag_color_hex":
                    settings.TagColorHex = value;
                    break;
                case "tag_opacity_percent":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tagOpacityPercent))
                    {
                        settings.TagOpacityPercent = tagOpacityPercent;
                    }
                    break;
                case "phrase_color_hex":
                    settings.PhraseColorHex = value;
                    break;
                case "phrase_opacity_percent":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var phraseOpacityPercent))
                    {
                        settings.PhraseOpacityPercent = phraseOpacityPercent;
                    }
                    break;
                case "sidebar_color_hex":
                    settings.SidebarColorHex = value;
                    break;
                case "sidebar_opacity_percent":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sidebarOpacityPercent))
                    {
                        settings.SidebarOpacityPercent = sidebarOpacityPercent;
                    }
                    break;
                case "search_overlay_color_hex":
                    settings.SearchOverlayColorHex = value;
                    break;
                case "search_overlay_opacity_percent":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var searchOverlayOpacityPercent))
                    {
                        settings.SearchOverlayOpacityPercent = searchOverlayOpacityPercent;
                    }
                    break;
                case "search_input_color_hex":
                    settings.SearchInputColorHex = value;
                    break;
                case "search_input_opacity_percent":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var searchInputOpacityPercent))
                    {
                        settings.SearchInputOpacityPercent = searchInputOpacityPercent;
                    }
                    break;
            }
        }

        await ApplySensitiveSettingsAsync(settings, legacyAiApiKey, legacySyncApiToken, connection).ConfigureAwait(false);
        settings.EnsureScreenshotHotkeyMigrationApplied();
        await NormalizeServiceEndpointSettingsAsync(settings, connection).ConfigureAwait(false);

        return settings;
    }

    private async Task ApplySensitiveSettingsAsync(
        AppSettings settings,
        string legacyAiApiKey,
        string legacySyncApiToken,
        SqliteConnection connection)
    {
        var storedSecrets = await _secretStore.LoadSecretsAsync().ConfigureAwait(false);
        var effectiveAiApiKey = storedSecrets.TryGetValue(AiApiKeySettingKey, out var storedAiApiKey) && !string.IsNullOrWhiteSpace(storedAiApiKey)
            ? storedAiApiKey
            : legacyAiApiKey?.Trim() ?? string.Empty;
        var effectiveSyncApiToken = storedSecrets.TryGetValue(SyncApiTokenSettingKey, out var storedSyncApiToken) && !string.IsNullOrWhiteSpace(storedSyncApiToken)
            ? storedSyncApiToken
            : legacySyncApiToken?.Trim() ?? string.Empty;

        settings.AiApiKey = effectiveAiApiKey;
        settings.SyncApiToken = effectiveSyncApiToken;

        if (!string.IsNullOrWhiteSpace(legacyAiApiKey) ||
            !string.IsNullOrWhiteSpace(legacySyncApiToken))
        {
            await _secretStore.SaveSecretsAsync(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AiApiKeySettingKey] = effectiveAiApiKey,
                [SyncApiTokenSettingKey] = effectiveSyncApiToken
            }).ConfigureAwait(false);

            await ClearLegacySecretsAsync(connection).ConfigureAwait(false);
        }
    }

    private static async Task ClearLegacySecretsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE app_settings
            SET value = ''
            WHERE key IN ($aiApiKey, $syncApiToken);
            """;
        command.Parameters.AddWithValue("$aiApiKey", AiApiKeySettingKey);
        command.Parameters.AddWithValue("$syncApiToken", SyncApiTokenSettingKey);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task NormalizeServiceEndpointSettingsAsync(AppSettings settings, SqliteConnection connection)
    {
        var effectiveServerBaseUrl = SettingsInputValidator.ResolveOperationalServerBaseUrl(settings.ServerBaseUrl);
        if (string.Equals(settings.ServerBaseUrl, effectiveServerBaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        settings.ServerBaseUrl = effectiveServerBaseUrl;
        await UpsertAppSettingAsync(connection, "server_base_url", effectiveServerBaseUrl).ConfigureAwait(false);
    }

    private static async Task UpsertAppSettingAsync(SqliteConnection connection, string key, string value)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO app_settings (key, value)
            VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static string ToDbDateTime(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseDbDateTime(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static long ParseInt64(string value, long fallback, long min, long max)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return fallback;
        }

        return Math.Clamp(parsed, min, max);
    }

    private static IReadOnlyList<string> DeserializeStringList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(value) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
