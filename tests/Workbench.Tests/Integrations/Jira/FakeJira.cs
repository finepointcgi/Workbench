using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Workbench.Application.Integrations.Jira;

namespace Workbench.Tests.Integrations.Jira;

internal sealed class FakeJiraUser(string accountId, string name, string? email, bool emailVisible = true)
{
    public string AccountId { get; } = accountId;
    public string Name { get; } = name;
    public string? Email { get; } = email;
    public bool EmailVisible { get; } = emailVisible;
}

internal sealed class FakeJiraIssue
{
    public required string Id { get; init; }
    public required string Key { get; init; }
    public required string ProjectKey { get; init; }
    public string Summary { get; set; } = "";
    public string Status { get; set; } = "To Do";
    public string Category { get; set; } = "new";
    public string? AssigneeAccountId { get; set; }
    public DateOnly? DueDate { get; set; }
    public long? EstimateSeconds { get; set; }
    public string? Parent { get; set; }
    public List<string> Labels { get; } = [];
    public DateTime Updated { get; set; }
}

/// <summary>A Jira Cloud site in memory answering the REST v3 calls the sync makes. Tests change Jira's side through <see cref="Issues"/>, queue failures with <see cref="Fail"/>, and read <see cref="Requests"/>.</summary>
internal sealed class FakeJira(Func<DateTime> now) : HttpMessageHandler, IJiraHandlerFactory
{
    public const string Email = "bot@example.com";
    public const string Token = "jira-test-token-0123456789";
    public const string ProjectKey = "RAD";

    private int _nextId = 10000;
    private int _nextNumber = 100;
    private readonly List<(string Method, Regex Path, HttpStatusCode Status, int Times)> _failures = [];

    public ConcurrentQueue<(string Method, string Path, string? Body)> Requests { get; } = new();
    public List<FakeJiraIssue> Issues { get; } = [];
    public List<FakeJiraUser> Users { get; } =
    [
        new("acc-bot", "Workbench Bot", Email),
        new("acc-jordan", "Jordan Lee", "jlee@example.com"),
        new("acc-hidden", "Sam Park", null),
    ];

    /// <summary>The "Done" transition is missing from the workflow (a required step in between).</summary>
    public bool NoDoneTransition { get; set; }

    public bool Down { get; set; }

    public HttpMessageHandler Get(Uri siteUrl) => this;

    public void Fail(string method, string pathPattern, HttpStatusCode status, int times = 1) => _failures.Add((method, new Regex(pathPattern), status, times));

    public FakeJiraIssue Add(string summary, string? assignee = null, DateOnly? due = null, bool done = false, long? estimateSeconds = null, string? parent = null, string project = ProjectKey)
    {
        var number = _nextNumber++;
        var issue = new FakeJiraIssue
        {
            Id = (_nextId++).ToString(), Key = $"{project}-{number}", ProjectKey = project, Summary = summary, AssigneeAccountId = assignee, DueDate = due,
            EstimateSeconds = estimateSeconds, Parent = parent, Status = done ? "Done" : "To Do", Category = done ? "done" : "new", Updated = now(),
        };
        Issues.Add(issue);
        return issue;
    }

    public FakeJiraIssue Issue(string key) => Issues.Single(i => i.Key == key);

    public void SetDone(FakeJiraIssue issue, bool done)
    {
        issue.Status = done ? "Done" : "To Do";
        issue.Category = done ? "done" : "new";
        issue.Updated = now();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue((request.Method.Method, path + request.RequestUri.Query, body));
        if (Down)
        {
            throw new HttpRequestException("connection refused");
        }

        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Email}:{Token}"));
        if (request.Headers.Authorization is not { Scheme: "Basic" } auth || auth.Parameter != expected)
        {
            return Json(HttpStatusCode.Unauthorized, new { message = "Unauthorized" });
        }

        foreach (var f in _failures.ToList())
        {
            if (f.Method == request.Method.Method && f.Path.IsMatch(path))
            {
                _failures.Remove(f);
                if (f.Times > 1)
                {
                    _failures.Add(f with { Times = f.Times - 1 });
                }

                return Json(f.Status, new { errorMessages = new[] { "forced failure" } });
            }
        }

        const string api = "/rest/api/3/";
        var route = path[(path.IndexOf(api, StringComparison.Ordinal) + api.Length)..].TrimEnd('/');
        var method = request.Method.Method;
        if (route == "myself")
        {
            return Json(HttpStatusCode.OK, User(Users[0]));
        }

        if (route == "project/search")
        {
            return Json(HttpStatusCode.OK, new { values = new[] { new { key = ProjectKey, name = "Radar" } } });
        }

        if (route.StartsWith("project/", StringComparison.Ordinal))
        {
            var key = route["project/".Length..];
            return key == ProjectKey ? Json(HttpStatusCode.OK, new { key = ProjectKey, name = "Radar" }) : Json(HttpStatusCode.NotFound, new { errorMessages = new[] { "No project could be found." } });
        }

        if (route == "search/jql")
        {
            return Json(HttpStatusCode.OK, new { issues = Filter(query["jql"] ?? "").Select(Render).ToList(), isLast = true });
        }

        if (route == "user/search")
        {
            var q = query["query"] ?? "";
            return Json(HttpStatusCode.OK, Users.Where(u => u.Email is not null && u.Email.Equals(q, StringComparison.OrdinalIgnoreCase)).Select(u => User(u, forceEmail: true)).ToList());
        }

        if (route == "issue" && method == "POST")
        {
            var fields = JsonNode.Parse(body!)!["fields"]!;
            var issue = Add(fields["summary"]!.GetValue<string>(), due: fields["duedate"] is { } d ? DateOnly.Parse(d.GetValue<string>()) : null, project: fields["project"]!["key"]!.GetValue<string>());
            issue.Labels.AddRange(fields["labels"]!.AsArray().Select(l => l!.GetValue<string>()));
            return Json(HttpStatusCode.Created, new { id = issue.Id, key = issue.Key });
        }

        var match = Regex.Match(route, @"^issue/(?<key>[A-Z]+-\d+)(?<rest>/transitions)?$");
        if (!match.Success)
        {
            return Json(HttpStatusCode.NotFound, new { errorMessages = new[] { "unknown route " + route } });
        }

        var found = Issues.FirstOrDefault(i => i.Key == match.Groups["key"].Value);
        if (found is null)
        {
            return Json(HttpStatusCode.NotFound, new { errorMessages = new[] { "Issue does not exist or you do not have permission to see it." } });
        }

        if (match.Groups["rest"].Success)
        {
            if (method == "GET")
            {
                var list = new List<object>();
                if (!NoDoneTransition && found.Category != "done")
                {
                    list.Add(new { id = "31", name = "Done", to = new { name = "Done", statusCategory = new { key = "done" } } });
                }

                list.Add(new { id = "21", name = "Start", to = new { name = "In Progress", statusCategory = new { key = "indeterminate" } } });
                return Json(HttpStatusCode.OK, new { transitions = list });
            }

            var id = JsonNode.Parse(body!)!["transition"]!["id"]!.GetValue<string>();
            if (id == "31")
            {
                SetDone(found, true);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (method == "PUT")
        {
            var fields = JsonNode.Parse(body!)!["fields"]!.AsObject();
            if (fields.ContainsKey("duedate"))
            {
                found.DueDate = fields["duedate"] is { } d ? DateOnly.Parse(d.GetValue<string>()) : null;
            }

            found.Updated = now();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        return Json(HttpStatusCode.OK, Render(found));
    }

    private IEnumerable<FakeJiraIssue> Filter(string jql)
    {
        IEnumerable<FakeJiraIssue> issues = Issues;
        if (Regex.Match(jql, @"project = ""(?<v>[^""]+)""") is { Success: true } project)
        {
            issues = issues.Where(i => i.ProjectKey == project.Groups["v"].Value);
        }

        if (Regex.Match(jql, @"parent = ""(?<v>[^""]+)""") is { Success: true } parent)
        {
            issues = issues.Where(i => i.Parent == parent.Groups["v"].Value);
        }

        if (Regex.Match(jql, @"key = ""(?<v>[^""]+)""") is { Success: true } key)
        {
            issues = issues.Where(i => i.Key == key.Groups["v"].Value);
        }

        if (Regex.Match(jql, @"labels = ""(?<v>[^""]+)""") is { Success: true } label)
        {
            issues = issues.Where(i => i.Labels.Contains(label.Groups["v"].Value));
        }

        if (jql.Contains("updated >= -", StringComparison.Ordinal))
        {
            var minutes = int.Parse(Regex.Match(jql, @"updated >= -(?<v>\d+)m").Groups["v"].Value);
            issues = issues.Where(i => i.Updated >= now().AddMinutes(-minutes));
        }

        return issues.OrderBy(i => i.Updated).ToList();
    }

    private object Render(FakeJiraIssue i) => new
    {
        id = i.Id,
        key = i.Key,
        fields = new Dictionary<string, object?>
        {
            ["summary"] = i.Summary,
            ["status"] = new { name = i.Status, statusCategory = new { key = i.Category } },
            ["assignee"] = i.AssigneeAccountId is { } a ? User(Users.Single(u => u.AccountId == a)) : null,
            ["duedate"] = i.DueDate?.ToString("yyyy-MM-dd"),
            ["updated"] = i.Updated.ToString("yyyy-MM-ddTHH:mm:ss.fff+0000"),
            ["timeoriginalestimate"] = i.EstimateSeconds,
            ["labels"] = i.Labels,
        },
    };

    private static object User(FakeJiraUser u, bool forceEmail = false) =>
        new { accountId = u.AccountId, displayName = u.Name, emailAddress = u.EmailVisible || forceEmail ? u.Email : null, accountType = "atlassian" };

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
