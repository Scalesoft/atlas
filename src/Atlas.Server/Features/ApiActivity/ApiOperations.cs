namespace Atlas.Server.Features.ApiActivity;

/// <summary>The audited write operations of /api/v1 (plan D2: every route with the write scope). MCP records its tool names.</summary>
public static class ApiOperations
{
    public const string PageCreate = "page.create";
    public const string PageSave = "page.save";
    public const string PageRename = "page.rename";
    public const string PageRestore = "page.restore";
    public const string PageVersionName = "page.version_name";
    public const string PageUndoChange = "page.undo_change";
    public const string PagePublish = "page.publish";
    public const string PagePublicationSet = "page.publication_set";
    public const string PageMove = "page.move";
    public const string PageMoveSpace = "page.move_space";
    public const string PageTrash = "page.trash";
    public const string PageUntrash = "page.untrash";
    public const string TreeSort = "tree.sort";
    public const string AttachmentUpload = "attachment.upload";
    public const string Favorite = "page.favorite";
    public const string Visit = "page.visit";
    public const string PageDecorationSet = "page.decoration_set";
    public const string PageVerificationSet = "page.verification_set";
    public const string PageReportOutdated = "page.report_outdated";
    public const string SpaceVerificationBatch = "space.verification_batch";
    public const string CommentPost = "comment.post";
    public const string PageTagsSet = "page.tags_set";
    public const string PageTagsChange = "page.tags_change";
    public const string SkillRate = "skill.rate";
    public const string TopicMerge = "topic.merge";
    public const string TopicLink = "topic.link";
    public const string PageEmbedUpdate = "page.embed_update";
    public const string RequirementUpdate = "requirement.update";
    public const string RequirementUpdateBatch = "requirement.update_batch";
    public const string RequirementInsert = "requirement.insert";
    public const string RequirementCreate = "requirement.create";
    public const string RequirementInsertFromDeployment = "requirement.insert_from_deployment";
    public const string RequirementMove = "requirement.move";
    public const string RequirementRemove = "requirement.remove";
    public const string ProjectorCatalogSet = "projector.catalog_set";
    public const string ProjectorDeploymentCreate = "projector.deployment_create";
    public const string TreeReferenceAdd = "tree_reference.add";
    public const string TreeReferenceRemove = "tree_reference.remove";
    public const string TreeReferenceMove = "tree_reference.move";
    public const string BoardCreate = "board.create";
    public const string BoardDelete = "board.delete";
    public const string BoardEdit = "board.edit";
    public const string BoardMove = "board.move";
    public const string BoardUntrash = "board.untrash";
    public const string ObjectRestore = "object.restore";
    public const string ObjectCreate = "object.create";
    public const string ObjectChange = "object.change";
    public const string ObjectTrash = "object.trash";
    public const string NotificationsRead = "notifications.read";
    public const string JobRun = "job.run";
    public const string JobRetry = "job.retry";
    public const string JobCancel = "job.cancel";
    public const string JobPause = "job.pause";
}
