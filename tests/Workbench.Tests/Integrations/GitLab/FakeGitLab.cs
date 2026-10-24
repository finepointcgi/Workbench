using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Workbench.Application.Integrations.GitLab;

namespace Workbench.Tests.Integrations.GitLab;

/// <summary>
/// A GitLab server in memory, answering the REST (v4) and GraphQL calls the sync makes. Answers are built from recorded
/// response fixtures (Fixtures/*.json) with the fake's state written over them, so field names and shapes are GitLab's.
/// Tests change GitLab's side through <see cref="Issues"/>, <see cref="Epics"/> and <see cref="Milestones"/> (as a person
/// in GitLab would), queue failures with <see cref="Fail"/>, and read <see cref="Requests"/>.
/// </summary>
internal sealed partial class FakeGitLab(Func<DateTime> now) : HttpMessageHandler, IGitLabHandlerFactory
{
    public static readonly string FixtureFolder = Path.Combine(AppContext.BaseDirectory, "Integrations", "GitLab", "Fixtures");

    public const long ProjectId = 42;
    public const long GroupId = 7;
    public const string ProjectPath = "program-office/plan";
    public const string Token = "glpat-test-token-0123456789";

    private readonly Lock _gate = new();
    private long _nextId = 9000;

    /// <summary>The group has epics (Premium/Ultimate). False: a Free group, epics answer 404.</summary>
    public bool EpicsAvailable { get; set; } = true;

    /// <summary>The token's scopes as GitLab reports them.</summary>
    public string[] Scopes { get; set; } = ["api"];

    /// <summary>Whether the token may add webhooks (Maintainer).</summary>
    public bool CanAddHooks { get; set; } = true;

    public string BaseUrlSeen { get; private set; } = "";

    public ConcurrentQueue<(string Method, string Path, string? Body)> Requests { get; } = new();

    public List<FakeIssue> Issues { get; } = [];
    public List<FakeEpic> Epics { get; } = [];
    public List<FakeMilestone> Milestones { get; } = [];
    public List<FakeUser> Users { get; } = [new(101, "workbench-bot", "Workbench Bot", "bot@example.com")];
    public List<(long Id, string Url, string Token, bool Group)> Hooks { get; } = [];
    public List<(long Id, long SourceIid, long TargetIid, string Type)> Links { get; } = [];
    public List<(long Iid, string Body)> Notes { get; } = [];

    /// <summary>Child tasks (work items) by issue id, for progress.</summary>
    public Dictionary<long, List<(bool Closed, int? Weight)>> ChildTasks { get; } = [];

    private readonly List<(string Method, Regex Path, HttpStatusCode Status, string? RetryAfter, int Times)> _failures = [];

    public HttpMessageHandler Get(Uri baseUrl, string? caCertificatePem) => this;

    /// <summary>The next <paramref name="times"/> calls matching <paramref name="method"/> and <paramref name="pathPattern"/> answer <paramref name="status"/>.</summary>
    public void Fail(string method, string pathPattern, HttpStatusCode status, int times = 1, string? retryAfter = null)
    {
        lock (_gate)
        {
            _failures.Add((method, new Regex(pathPattern), status, retryAfter, times));
        }
    }

    public FakeIssue Issue(long iid) => Issues.Single(i => i.Iid == iid);

    public FakeIssue IssueTitled(string title) => Issues.Single(i => i.Title == title);

    public FakeUser AddUser(long id, string username, string name, string? email = null, string? publicEmail = null)
    {
        var user = new FakeUser(id, username, name, email, publicEmail);
        Users.Add(user);
        return user;
    }

    /// <summary>An issue someone created in GitLab.</summary>
    public FakeIssue AddIssue(string title, long? epicId = null, long? milestoneId = null, string? description = null)
    {
        lock (_gate)
        {
            var issue = new FakeIssue { Id = ++_nextId, Iid = Issues.Count + 1, Title = title, Description = description, EpicId = epicId, MilestoneId = milestoneId, UpdatedAt = now() };
            Issues.Add(issue);
            return issue;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var url = request.RequestUri!;
        var full = url.AbsolutePath;
        var marker = full.IndexOf("/api/", StringComparison.Ordinal);
        BaseUrlSeen = $"{url.Scheme}://{url.Authority}{full[..Math.Max(0, marker)]}";
        var path = marker < 0 ? full : full[(marker + 1)..];
        Requests.Enqueue((request.Method.Method, path + url.Query, body));

        if (!request.Headers.TryGetValues("PRIVATE-TOKEN", out var tokens) || tokens.FirstOrDefault() != Token)
        {
            return Json(HttpStatusCode.Unauthorized, """{"message":"401 Unauthorized"}""");
        }

        lock (_gate)
        {
            for (var i = 0; i < _failures.Count; i++)
            {
                var f = _failures[i];
                if (f.Method == request.Method.Method && f.Path.IsMatch(path))
                {
                    _failures[i] = f with { Times = f.Times - 1 };
                    if (_failures[i].Times <= 0)
                    {
                        _failures.RemoveAt(i);
                    }

                    var failed = Json(f.Status, $$"""{"message":"{{(int)f.Status}} from the fake"}""");
                    if (f.RetryAfter is not null)
                    {
                        failed.Headers.TryAddWithoutValidation("Retry-After", f.RetryAfter);
                    }

                    return failed;
                }
            }

            try
            {
                return Route(request.Method.Method, path, Query(url.Query), body is null || body.Length == 0 ? null : JsonNode.Parse(body)?.AsObject());
            }
            catch (KeyNotFoundException)
            {
                return Json(HttpStatusCode.NotFound, """{"message":"404 Not Found"}""");
            }
            catch (InvalidOperationException)
            {
                return Json(HttpStatusCode.NotFound, """{"message":"404 Not Found"}""");
            }
        }
    }

    private HttpResponseMessage Route(string method, string path, Dictionary<string, string> q, JsonObject? body)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (parts.Length >= 2 && parts[0] == "api" && parts[1] == "graphql")
        {
            return GraphQl(body!);
        }

        parts = parts.Skip(2).ToArray(); // api, v4
        switch (method, parts)
        {
            case ("GET", ["version"]):
                return Fixture("version.json");
            case ("GET", ["user"]):
                return Fixture("user.json");
            case ("GET", ["personal_access_tokens", "self"]):
                return Fixture("token-self.json", o => o["scopes"] = new JsonArray(Scopes.Select(s => (JsonNode)s).ToArray()));
            case ("GET", ["projects"]):
                return Array([ProjectJson()]);
            case ("GET", ["projects", var p]) when p is "42" or ProjectPath:
                return Ok(ProjectJson());
            case ("GET", ["projects", _]):
                throw new KeyNotFoundException();
            case ("GET", ["groups", _, "epics"]):
                if (!EpicsAvailable)
                {
                    throw new KeyNotFoundException();
                }

                return Array(Epics.Where(e => After(q, e.UpdatedAt)).OrderBy(e => e.UpdatedAt).Select(EpicJson));
            case ("POST", ["groups", _, "epics"]):
            {
                if (!EpicsAvailable)
                {
                    throw new KeyNotFoundException();
                }

                var epic = new FakeEpic { Id = 3000 + Epics.Count + 1, Iid = Epics.Count + 1, UpdatedAt = now() };
                ApplyEpic(epic, body!);
                Epics.Add(epic);
                return Ok(EpicJson(epic), HttpStatusCode.Created);
            }

            case ("GET", ["groups", _, "epics", var iid]):
                return Ok(EpicJson(Epics.Single(e => e.Iid == long.Parse(iid, CultureInfo.InvariantCulture))));
            case ("PUT", ["groups", _, "epics", var iid]):
            {
                var epic = Epics.Single(e => e.Iid == long.Parse(iid, CultureInfo.InvariantCulture));
                ApplyEpic(epic, body!);
                epic.UpdatedAt = now();
                return Ok(EpicJson(epic));
            }

            case ("GET", ["projects", _, "issues"]):
            {
                var list = Issues.Where(i => After(q, i.UpdatedAt));
                if (q.TryGetValue("search", out var search))
                {
                    list = list.Where(i => i.Description?.Contains(search, StringComparison.Ordinal) == true);
                }

                return Array(list.OrderBy(i => i.UpdatedAt).Select(IssueJson));
            }

            case ("POST", ["projects", _, "issues"]):
            {
                var issue = new FakeIssue { Id = ++_nextId, Iid = Issues.Count + 1, UpdatedAt = now() };
                ApplyIssue(issue, body!);
                Issues.Add(issue);
                return Ok(IssueJson(issue), HttpStatusCode.Created);
            }

            case ("GET", ["projects", _, "issues", var iid]):
                return Ok(IssueJson(Issue(long.Parse(iid, CultureInfo.InvariantCulture))));
            case ("PUT", ["projects", _, "issues", var iid]):
            {
                var issue = Issue(long.Parse(iid, CultureInfo.InvariantCulture));
                ApplyIssue(issue, body!);
                issue.UpdatedAt = now();
                return Ok(IssueJson(issue));
            }

            case ("POST", ["projects", _, "issues", var iid, "time_estimate"]):
            {
                var issue = Issue(long.Parse(iid, CultureInfo.InvariantCulture));
                issue.TimeEstimateSeconds = long.Parse(q["duration"].TrimEnd('m'), CultureInfo.InvariantCulture) * 60;
                return Ok(new JsonObject { ["time_estimate"] = issue.TimeEstimateSeconds });
            }

            case ("POST", ["projects", _, "issues", var iid, "reset_time_estimate"]):
                Issue(long.Parse(iid, CultureInfo.InvariantCulture)).TimeEstimateSeconds = 0;
                return Ok(new JsonObject { ["time_estimate"] = 0 });
            case ("POST", ["projects", _, "issues", var iid, "notes"]):
                Notes.Add((long.Parse(iid, CultureInfo.InvariantCulture), body!["body"]!.GetValue<string>()));
                return Ok(new JsonObject { ["id"] = Notes.Count }, HttpStatusCode.Created);
            case ("GET", ["projects", _, "issues", var iid, "links"]):
            {
                var me = long.Parse(iid, CultureInfo.InvariantCulture);
                return Array(Links.Where(l => l.SourceIid == me || l.TargetIid == me).Select(l =>
                {
                    var other = Issue(l.SourceIid == me ? l.TargetIid : l.SourceIid);
                    var json = IssueJson(other);
                    json["issue_link_id"] = l.Id;
                    json["link_type"] = l.Type == "blocks" ? l.SourceIid == me ? "blocks" : "is_blocked_by" : l.Type;
                    return json;
                }));
            }

            case ("POST", ["projects", _, "issues", var iid, "links"]):
            {
                var type = body!["link_type"]!.GetValue<string>();
                if (type == "blocks" && !EpicsAvailable)
                {
                    return Json(HttpStatusCode.Forbidden, """{"message":"Blocked issues not available for current license"}""");
                }

                Links.Add((Links.Count + 1, long.Parse(iid, CultureInfo.InvariantCulture), long.Parse(body["target_issue_iid"]!.GetValue<string>(), CultureInfo.InvariantCulture), type));
                return Ok(new JsonObject { ["id"] = Links.Count }, HttpStatusCode.Created);
            }

            case ("DELETE", ["projects", _, "issues", _, "links", var linkId]):
                Links.RemoveAll(l => l.Id == long.Parse(linkId, CultureInfo.InvariantCulture));
                return Ok(new JsonObject());
            case ("GET", ["projects", _, "milestones"]):
                return Array(Milestones.Select(MilestoneJson));
            case ("POST", ["projects", _, "milestones"]):
            {
                var title = body!["title"]!.GetValue<string>();
                if (Milestones.Any(m => m.Title == title))
                {
                    return Json(HttpStatusCode.BadRequest, """{"message":"Title has already been taken"}""");
                }

                var milestone = new FakeMilestone { Id = 5000 + Milestones.Count + 1, Iid = Milestones.Count + 1, UpdatedAt = now() };
                ApplyMilestone(milestone, body);
                Milestones.Add(milestone);
                return Ok(MilestoneJson(milestone), HttpStatusCode.Created);
            }

            case ("GET", ["projects", _, "milestones", var id]):
                return Ok(MilestoneJson(Milestones.Single(m => m.Id == long.Parse(id, CultureInfo.InvariantCulture))));
            case ("PUT", ["projects", _, "milestones", var id]):
            {
                var milestone = Milestones.Single(m => m.Id == long.Parse(id, CultureInfo.InvariantCulture));
                ApplyMilestone(milestone, body!);
                milestone.UpdatedAt = now();
                return Ok(MilestoneJson(milestone));
            }

            case ("GET", ["users"]):
            {
                IEnumerable<FakeUser> found = q.TryGetValue("username", out var username)
                    ? Users.Where(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase))
                    : Users.Where(u => q.TryGetValue("search", out var s) && (string.Equals(u.Email, s, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(u.PublicEmail, s, StringComparison.OrdinalIgnoreCase) || u.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
                        || u.Username.Contains(s, StringComparison.OrdinalIgnoreCase)));
                return Array(found.Select(UserJson));
            }

            case ("GET", ["users", var id]):
                return Ok(UserJson(Users.Single(u => u.Id == long.Parse(id, CultureInfo.InvariantCulture))));
            case ("GET", ["projects", _, "members", "all"]):
                return Array(Users.Where(u => !q.TryGetValue("query", out var s) || u.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
                    || u.Username.Contains(s, StringComparison.OrdinalIgnoreCase)).Select(UserJson));
            case ("POST", [var scope, _, "hooks"]):
                if (!CanAddHooks)
                {
                    return Json(HttpStatusCode.Forbidden, """{"message":"403 Forbidden"}""");
                }

                Hooks.Add((Hooks.Count + 1, body!["url"]!.GetValue<string>(), body["token"]!.GetValue<string>(), scope == "groups"));
                return Ok(new JsonObject { ["id"] = Hooks.Count, ["url"] = body["url"]!.GetValue<string>() }, HttpStatusCode.Created);
            case ("DELETE", [_, _, "hooks", var id]):
                Hooks.RemoveAll(h => h.Id == long.Parse(id, CultureInfo.InvariantCulture));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            default:
                throw new KeyNotFoundException();
        }
    }

    private HttpResponseMessage GraphQl(JsonObject body)
    {
        var gid = body["variables"]!["id"]!.GetValue<string>();
        var id = long.Parse(gid[(gid.LastIndexOf('/') + 1)..], CultureInfo.InvariantCulture);
        var nodes = new JsonArray();
        foreach (var (closed, weight) in ChildTasks.GetValueOrDefault(id) ?? [])
        {
            var widgets = new JsonArray(new JsonObject { ["weight"] = weight });
            nodes.Add(new JsonObject { ["state"] = closed ? "CLOSED" : "OPEN", ["widgets"] = widgets });
        }

        var hierarchy = new JsonObject { ["children"] = new JsonObject { ["nodes"] = nodes } };
        return Ok(new JsonObject { ["data"] = new JsonObject { ["workItem"] = new JsonObject { ["widgets"] = new JsonArray(new JsonObject(), hierarchy) } } });
    }

    // ---- applying writes

    private static void ApplyIssue(FakeIssue issue, JsonObject body)
    {
        if (body["title"] is { } t)
        {
            issue.Title = t.GetValue<string>();
        }

        if (body.ContainsKey("description"))
        {
            issue.Description = body["description"]?.GetValue<string>();
        }

        if (body["assignee_ids"] is JsonArray ids)
        {
            issue.AssigneeIds = ids.Select(i => i!.GetValue<long>()).ToList();
        }

        if (body.ContainsKey("due_date"))
        {
            issue.DueDate = body["due_date"] is { } d ? DateOnly.Parse(d.GetValue<string>(), CultureInfo.InvariantCulture) : null;
        }

        if (body["labels"] is { } labels)
        {
            issue.Labels = labels.GetValue<string>().Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        if (body["remove_labels"] is { } remove)
        {
            issue.Labels.RemoveAll(l => remove.GetValue<string>().Split(',').Contains(l));
        }

        if (body["add_labels"] is { } add)
        {
            foreach (var l in add.GetValue<string>().Split(','))
            {
                if (!issue.Labels.Contains(l))
                {
                    issue.Labels.Add(l);
                }
            }
        }

        if (body.ContainsKey("milestone_id"))
        {
            issue.MilestoneId = body["milestone_id"]?.GetValue<long>();
        }

        if (body.ContainsKey("epic_id"))
        {
            issue.EpicId = body["epic_id"]?.GetValue<long>();
        }

        if (body["state_event"] is { } state)
        {
            issue.State = state.GetValue<string>() == "close" ? "closed" : "opened";
        }
    }

    private static void ApplyEpic(FakeEpic epic, JsonObject body)
    {
        if (body["title"] is { } t)
        {
            epic.Title = t.GetValue<string>();
        }

        if (body.ContainsKey("description"))
        {
            epic.Description = body["description"]?.GetValue<string>();
        }

        if (body.ContainsKey("start_date_fixed"))
        {
            epic.Start = body["start_date_fixed"] is { } s ? DateOnly.Parse(s.GetValue<string>(), CultureInfo.InvariantCulture) : null;
        }

        if (body.ContainsKey("due_date_fixed"))
        {
            epic.Due = body["due_date_fixed"] is { } d ? DateOnly.Parse(d.GetValue<string>(), CultureInfo.InvariantCulture) : null;
        }

        if (body.ContainsKey("parent_id"))
        {
            epic.ParentId = body["parent_id"]?.GetValue<long>();
        }

        if (body["labels"] is { } labels)
        {
            epic.Labels = labels.GetValue<string>().Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        if (body["add_labels"] is { } add)
        {
            epic.Labels = epic.Labels.Where(l => body["remove_labels"]?.GetValue<string>().Split(',').Contains(l) != true).Concat(add.GetValue<string>().Split(',')).Distinct().ToList();
        }

        if (body["state_event"] is { } state)
        {
            epic.State = state.GetValue<string>() == "close" ? "closed" : "opened";
        }
    }

    private static void ApplyMilestone(FakeMilestone milestone, JsonObject body)
    {
        if (body["title"] is { } t)
        {
            milestone.Title = t.GetValue<string>();
        }

        if (body.ContainsKey("description"))
        {
            milestone.Description = body["description"]?.GetValue<string>();
        }

        if (body.ContainsKey("start_date"))
        {
            milestone.Start = body["start_date"] is { } s ? DateOnly.Parse(s.GetValue<string>(), CultureInfo.InvariantCulture) : null;
        }

        if (body.ContainsKey("due_date"))
        {
            milestone.Due = body["due_date"] is { } d ? DateOnly.Parse(d.GetValue<string>(), CultureInfo.InvariantCulture) : null;
        }

        if (body["state_event"] is { } state)
        {
            milestone.State = state.GetValue<string>() == "close" ? "closed" : "active";
        }
    }

    // ---- answers from the fixtures

    private JsonObject ProjectJson()
    {
        var o = Load("project.json");
        if (!EpicsAvailable)
        {
            // A Free group still has a namespace; epics just aren't there.
        }

        return o;
    }

    private JsonObject IssueJson(FakeIssue i)
    {
        var o = Load("issue.json");
        o["id"] = i.Id;
        o["iid"] = i.Iid;
        o["title"] = i.Title;
        o["description"] = i.Description;
        o["state"] = i.State;
        o["updated_at"] = Stamp(i.UpdatedAt);
        o["due_date"] = i.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        o["labels"] = new JsonArray(i.Labels.Select(l => (JsonNode)l).ToArray());
        o["assignees"] = new JsonArray(i.AssigneeIds.Select(id => (JsonNode)UserJson(Users.Single(u => u.Id == id))).ToArray());
        o["web_url"] = $"https://gitlab.example.com/{ProjectPath}/-/issues/{i.Iid}";
        o["time_stats"]!["time_estimate"] = i.TimeEstimateSeconds;
        o["task_completion_status"]!["count"] = i.ChecklistCount;
        o["task_completion_status"]!["completed_count"] = i.ChecklistDone;
        o["milestone"] = i.MilestoneId is { } mid && Milestones.FirstOrDefault(m => m.Id == mid) is { } m
            ? new JsonObject { ["id"] = m.Id, ["iid"] = m.Iid, ["title"] = m.Title, ["project_id"] = ProjectId }
            : null;
        o["epic"] = i.EpicId is { } eid && Epics.FirstOrDefault(e => e.Id == eid) is { } e
            ? new JsonObject { ["id"] = e.Id, ["iid"] = e.Iid, ["group_id"] = GroupId, ["title"] = e.Title }
            : null;
        return o;
    }

    private static JsonObject EpicJson(FakeEpic e)
    {
        var o = Load("epic.json");
        o["id"] = e.Id;
        o["iid"] = e.Iid;
        o["title"] = e.Title;
        o["description"] = e.Description;
        o["state"] = e.State;
        o["parent_id"] = e.ParentId;
        o["start_date_fixed"] = e.Start?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        o["due_date_fixed"] = e.Due?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        o["labels"] = new JsonArray(e.Labels.Select(l => (JsonNode)l).ToArray());
        o["updated_at"] = Stamp(e.UpdatedAt);
        o["web_url"] = $"https://gitlab.example.com/groups/program-office/-/epics/{e.Iid}";
        return o;
    }

    private static JsonObject MilestoneJson(FakeMilestone m)
    {
        var o = Load("milestone.json");
        o["id"] = m.Id;
        o["iid"] = m.Iid;
        o["title"] = m.Title;
        o["description"] = m.Description;
        o["state"] = m.State;
        o["start_date"] = m.Start?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        o["due_date"] = m.Due?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        o["updated_at"] = Stamp(m.UpdatedAt);
        o["web_url"] = $"https://gitlab.example.com/{ProjectPath}/-/milestones/{m.Iid}";
        return o;
    }

    private static JsonObject UserJson(FakeUser u)
    {
        var o = Load("user.json");
        o["id"] = u.Id;
        o["username"] = u.Username;
        o["name"] = u.Name;
        o["email"] = u.Email;
        o["public_email"] = u.PublicEmail;
        o["web_url"] = $"https://gitlab.example.com/{u.Username}";
        return o;
    }

    public static JsonObject Load(string fixture) => JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureFolder, fixture)))!.AsObject();

    /// <summary>A webhook payload fixture with fields replaced (dotted paths, e.g. <c>object_attributes.iid</c>).</summary>
    public static string Payload(string fixture, params (string Path, JsonNode? Value)[] values)
    {
        var o = Load(fixture);
        foreach (var (path, value) in values)
        {
            var parts = path.Split('.');
            var node = o;
            foreach (var p in parts[..^1])
            {
                node = node[p]!.AsObject();
            }

            node[parts[^1]] = value;
        }

        return o.ToJsonString();
    }

    private static HttpResponseMessage Fixture(string name, Action<JsonObject>? change = null)
    {
        var o = Load(name);
        change?.Invoke(o);
        return Ok(o);
    }

    private static bool After(Dictionary<string, string> q, DateTime at) =>
        !q.TryGetValue("updated_after", out var after) || at > DateTime.Parse(after, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static string Stamp(DateTime at) => DateTime.SpecifyKind(at, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static Dictionary<string, string> Query(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .GroupBy(p => Uri.UnescapeDataString(p[0]))
            .ToDictionary(g => g.Key, g => Uri.UnescapeDataString(g.Last().Length > 1 ? g.Last()[1] : ""));

    private static HttpResponseMessage Ok(JsonNode node, HttpStatusCode status = HttpStatusCode.OK) => Json(status, node.ToJsonString());

    private static HttpResponseMessage Array(IEnumerable<JsonObject> items) => Ok(new JsonArray(items.Select(i => (JsonNode)i).ToArray()));

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal sealed class FakeIssue
{
    public long Id { get; set; }
    public long Iid { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string State { get; set; } = "opened";
    public DateOnly? DueDate { get; set; }
    public List<string> Labels { get; set; } = [];
    public List<long> AssigneeIds { get; set; } = [];
    public long? MilestoneId { get; set; }
    public long? EpicId { get; set; }
    public long TimeEstimateSeconds { get; set; }
    public int ChecklistCount { get; set; }
    public int ChecklistDone { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>The description without Workbench's block.</summary>
    public string? Notes => GitLabRules.Strip(Description);
}

internal sealed class FakeEpic
{
    public long Id { get; set; }
    public long Iid { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string State { get; set; } = "opened";
    public DateOnly? Start { get; set; }
    public DateOnly? Due { get; set; }
    public long? ParentId { get; set; }
    public List<string> Labels { get; set; } = [];
    public DateTime UpdatedAt { get; set; }
}

internal sealed class FakeMilestone
{
    public long Id { get; set; }
    public long Iid { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string State { get; set; } = "active";
    public DateOnly? Start { get; set; }
    public DateOnly? Due { get; set; }
    public DateTime UpdatedAt { get; set; }
}

internal sealed record FakeUser(long Id, string Username, string Name, string? Email = null, string? PublicEmail = null);
