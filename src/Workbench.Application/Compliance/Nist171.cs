namespace Workbench.Application.Compliance;

/// <summary>One of the 110 security requirements in NIST SP 800-171 Revision 2 (the basis of CMMC Level 2), with its SPRS deduction.</summary>
public sealed record Nist171Requirement(string Id, int FamilyNumber, string Statement, int Weight, bool IsBasic)
{
    public string Family => Nist171.Families[FamilyNumber - 1];

    /// <summary>3.5.3 (multifactor authentication) and 3.13.11 (FIPS-validated cryptography) lose 3 points instead of 5 when partly met.</summary>
    public bool AllowsPartialCredit => Id is "3.5.3" or "3.13.11";
}

/// <summary>
/// The requirements as published in NIST SP 800-171 Rev 2 (statements shortened only by dropping footnote marks), and the point
/// values from the DoD "NIST SP 800-171 DoD Assessment Methodology" version 1.2.1 (June 24, 2020): 5, 3 or 1 point per
/// requirement, subtracted from 110 for each one not implemented. 3.12.4, the system security plan, carries no points: the
/// methodology says that without a plan no assessment can be made.
/// </summary>
public static class Nist171
{
    public const int PerfectScore = 110;

    /// <summary>What a partly met 3.5.3 or 3.13.11 loses.</summary>
    public const int PartialDeduction = 3;

    public static readonly IReadOnlyList<string> Families =
    [
        "Access Control",
        "Awareness and Training",
        "Audit and Accountability",
        "Configuration Management",
        "Identification and Authentication",
        "Incident Response",
        "Maintenance",
        "Media Protection",
        "Personnel Security",
        "Physical Protection",
        "Risk Assessment",
        "Security Assessment",
        "System and Communications Protection",
        "System and Information Integrity",
    ];

    public static readonly IReadOnlyList<Nist171Requirement> Requirements =
    [
        new("3.1.1", 1, "Limit system access to authorized users, processes acting on behalf of authorized users, and devices (including other systems).", 5, true),
        new("3.1.2", 1, "Limit system access to the types of transactions and functions that authorized users are permitted to execute.", 5, true),
        new("3.1.3", 1, "Control the flow of CUI in accordance with approved authorizations.", 1, false),
        new("3.1.4", 1, "Separate the duties of individuals to reduce the risk of malevolent activity without collusion.", 1, false),
        new("3.1.5", 1, "Employ the principle of least privilege, including for specific security functions and privileged accounts.", 3, false),
        new("3.1.6", 1, "Use non-privileged accounts or roles when accessing nonsecurity functions.", 1, false),
        new("3.1.7", 1, "Prevent non-privileged users from executing privileged functions and capture the execution of such functions in audit logs.", 1, false),
        new("3.1.8", 1, "Limit unsuccessful logon attempts.", 1, false),
        new("3.1.9", 1, "Provide privacy and security notices consistent with applicable CUI rules.", 1, false),
        new("3.1.10", 1, "Use session lock with pattern-hiding displays to prevent access and viewing of data after a period of inactivity.", 1, false),
        new("3.1.11", 1, "Terminate (automatically) a user session after a defined condition.", 1, false),
        new("3.1.12", 1, "Monitor and control remote access sessions.", 5, false),
        new("3.1.13", 1, "Employ cryptographic mechanisms to protect the confidentiality of remote access sessions.", 5, false),
        new("3.1.14", 1, "Route remote access via managed access control points.", 1, false),
        new("3.1.15", 1, "Authorize remote execution of privileged commands and remote access to security-relevant information.", 1, false),
        new("3.1.16", 1, "Authorize wireless access prior to allowing such connections.", 5, false),
        new("3.1.17", 1, "Protect wireless access using authentication and encryption.", 5, false),
        new("3.1.18", 1, "Control connection of mobile devices.", 5, false),
        new("3.1.19", 1, "Encrypt CUI on mobile devices and mobile computing platforms.", 3, false),
        new("3.1.20", 1, "Verify and control/limit connections to and use of external systems.", 1, false),
        new("3.1.21", 1, "Limit use of portable storage devices on external systems.", 1, false),
        new("3.1.22", 1, "Control CUI posted or processed on publicly accessible systems.", 1, false),
        new("3.2.1", 2, "Ensure that managers, systems administrators, and users of organizational systems are made aware of the security risks associated with their activities and of the applicable policies, standards, and procedures related to the security of those systems.", 5, true),
        new("3.2.2", 2, "Ensure that personnel are trained to carry out their assigned information security-related duties and responsibilities.", 5, true),
        new("3.2.3", 2, "Provide security awareness training on recognizing and reporting potential indicators of insider threat.", 1, false),
        new("3.3.1", 3, "Create and retain system audit logs and records to the extent needed to enable the monitoring, analysis, investigation, and reporting of unlawful or unauthorized system activity.", 5, true),
        new("3.3.2", 3, "Ensure that the actions of individual system users can be uniquely traced to those users, so they can be held accountable for their actions.", 3, true),
        new("3.3.3", 3, "Review and update logged events.", 1, false),
        new("3.3.4", 3, "Alert in the event of an audit logging process failure.", 1, false),
        new("3.3.5", 3, "Correlate audit record review, analysis, and reporting processes for investigation and response to indications of unlawful, unauthorized, suspicious, or unusual activity.", 5, false),
        new("3.3.6", 3, "Provide audit record reduction and report generation to support on-demand analysis and reporting.", 1, false),
        new("3.3.7", 3, "Provide a system capability that compares and synchronizes internal system clocks with an authoritative source to generate time stamps for audit records.", 1, false),
        new("3.3.8", 3, "Protect audit information and audit logging tools from unauthorized access, modification, and deletion.", 1, false),
        new("3.3.9", 3, "Limit management of audit logging functionality to a subset of privileged users.", 1, false),
        new("3.4.1", 4, "Establish and maintain baseline configurations and inventories of organizational systems (including hardware, software, firmware, and documentation) throughout the respective system development life cycles.", 5, true),
        new("3.4.2", 4, "Establish and enforce security configuration settings for information technology products employed in organizational systems.", 5, true),
        new("3.4.3", 4, "Track, review, approve or disapprove, and log changes to organizational systems.", 1, false),
        new("3.4.4", 4, "Analyze the security impact of changes prior to implementation.", 1, false),
        new("3.4.5", 4, "Define, document, approve, and enforce physical and logical access restrictions associated with changes to organizational systems.", 5, false),
        new("3.4.6", 4, "Employ the principle of least functionality by configuring organizational systems to provide only essential capabilities.", 5, false),
        new("3.4.7", 4, "Restrict, disable, or prevent the use of nonessential programs, functions, ports, protocols, and services.", 5, false),
        new("3.4.8", 4, "Apply deny-by-exception (blacklisting) policy to prevent the use of unauthorized software or deny-all, permit-by-exception (whitelisting) policy to allow the execution of authorized software.", 5, false),
        new("3.4.9", 4, "Control and monitor user-installed software.", 1, false),
        new("3.5.1", 5, "Identify system users, processes acting on behalf of users, and devices.", 5, true),
        new("3.5.2", 5, "Authenticate (or verify) the identities of users, processes, or devices, as a prerequisite to allowing access to organizational systems.", 5, true),
        new("3.5.3", 5, "Use multifactor authentication for local and network access to privileged accounts and for network access to non-privileged accounts.", 5, false),
        new("3.5.4", 5, "Employ replay-resistant authentication mechanisms for network access to privileged and non- privileged accounts.", 1, false),
        new("3.5.5", 5, "Prevent reuse of identifiers for a defined period.", 1, false),
        new("3.5.6", 5, "Disable identifiers after a defined period of inactivity.", 1, false),
        new("3.5.7", 5, "Enforce a minimum password complexity and change of characters when new passwords are created.", 1, false),
        new("3.5.8", 5, "Prohibit password reuse for a specified number of generations.", 1, false),
        new("3.5.9", 5, "Allow temporary password use for system logons with an immediate change to a permanent password.", 1, false),
        new("3.5.10", 5, "Store and transmit only cryptographically-protected passwords.", 5, false),
        new("3.5.11", 5, "Obscure feedback of authentication information.", 1, false),
        new("3.6.1", 6, "Establish an operational incident-handling capability for organizational systems that includes preparation, detection, analysis, containment, recovery, and user response activities.", 5, true),
        new("3.6.2", 6, "Track, document, and report incidents to designated officials and/or authorities both internal and external to the organization.", 5, true),
        new("3.6.3", 6, "Test the organizational incident response capability.", 1, false),
        new("3.7.1", 7, "Perform maintenance on organizational systems.", 3, true),
        new("3.7.2", 7, "Provide controls on the tools, techniques, mechanisms, and personnel used to conduct system maintenance.", 5, true),
        new("3.7.3", 7, "Ensure equipment removed for off-site maintenance is sanitized of any CUI.", 1, false),
        new("3.7.4", 7, "Check media containing diagnostic and test programs for malicious code before the media are used in organizational systems.", 3, false),
        new("3.7.5", 7, "Require multifactor authentication to establish nonlocal maintenance sessions via external network connections and terminate such connections when nonlocal maintenance is complete.", 5, false),
        new("3.7.6", 7, "Supervise the maintenance activities of maintenance personnel without required access authorization.", 1, false),
        new("3.8.1", 8, "Protect (i.e., physically control and securely store) system media containing CUI, both paper and digital.", 3, true),
        new("3.8.2", 8, "Limit access to CUI on system media to authorized users.", 3, true),
        new("3.8.3", 8, "Sanitize or destroy system media containing CUI before disposal or release for reuse.", 5, true),
        new("3.8.4", 8, "Mark media with necessary CUI markings and distribution limitations.", 1, false),
        new("3.8.5", 8, "Control access to media containing CUI and maintain accountability for media during transport outside of controlled areas.", 1, false),
        new("3.8.6", 8, "Implement cryptographic mechanisms to protect the confidentiality of CUI stored on digital media during transport unless otherwise protected by alternative physical safeguards.", 1, false),
        new("3.8.7", 8, "Control the use of removable media on system components.", 5, false),
        new("3.8.8", 8, "Prohibit the use of portable storage devices when such devices have no identifiable owner.", 3, false),
        new("3.8.9", 8, "Protect the confidentiality of backup CUI at storage locations.", 1, false),
        new("3.9.1", 9, "Screen individuals prior to authorizing access to organizational systems containing CUI.", 3, true),
        new("3.9.2", 9, "Ensure that organizational systems containing CUI are protected during and after personnel actions such as terminations and transfers.", 5, true),
        new("3.10.1", 10, "Limit physical access to organizational systems, equipment, and the respective operating environments to authorized individuals.", 5, true),
        new("3.10.2", 10, "Protect and monitor the physical facility and support infrastructure for organizational systems.", 5, true),
        new("3.10.3", 10, "Escort visitors and monitor visitor activity.", 1, false),
        new("3.10.4", 10, "Maintain audit logs of physical access.", 1, false),
        new("3.10.5", 10, "Control and manage physical access devices.", 1, false),
        new("3.10.6", 10, "Enforce safeguarding measures for CUI at alternate work sites.", 1, false),
        new("3.11.1", 11, "Periodically assess the risk to organizational operations (including mission, functions, image, or reputation), organizational assets, and individuals, resulting from the operation of organizational systems and the associated processing, storage, or transmission of CUI.", 3, true),
        new("3.11.2", 11, "Scan for vulnerabilities in organizational systems and applications periodically and when new vulnerabilities affecting those systems and applications are identified.", 5, false),
        new("3.11.3", 11, "Remediate vulnerabilities in accordance with risk assessments.", 1, false),
        new("3.12.1", 12, "Periodically assess the security controls in organizational systems to determine if the controls are effective in their application.", 5, true),
        new("3.12.2", 12, "Develop and implement plans of action designed to correct deficiencies and reduce or eliminate vulnerabilities in organizational systems.", 3, true),
        new("3.12.3", 12, "Monitor security controls on an ongoing basis to ensure the continued effectiveness of the controls.", 5, true),
        new("3.12.4", 12, "Develop, document, and periodically update system security plans that describe system boundaries, system environments of operation, how security requirements are implemented, and the relationships with or connections to other systems.", 0, false),
        new("3.13.1", 13, "Monitor, control, and protect communications (i.e., information transmitted or received by organizational systems) at the external boundaries and key internal boundaries of organizational systems.", 5, true),
        new("3.13.2", 13, "Employ architectural designs, software development techniques, and systems engineering principles that promote effective information security within organizational systems.", 5, true),
        new("3.13.3", 13, "Separate user functionality from system management functionality.", 1, false),
        new("3.13.4", 13, "Prevent unauthorized and unintended information transfer via shared system resources.", 1, false),
        new("3.13.5", 13, "Implement subnetworks for publicly accessible system components that are physically or logically separated from internal networks.", 5, false),
        new("3.13.6", 13, "Deny network communications traffic by default and allow network communications traffic by exception (i.e., deny all, permit by exception).", 5, false),
        new("3.13.7", 13, "Prevent remote devices from simultaneously establishing non-remote connections with organizational systems and communicating via some other connection to resources in external networks (i.e., split tunneling).", 1, false),
        new("3.13.8", 13, "Implement cryptographic mechanisms to prevent unauthorized disclosure of CUI during transmission unless otherwise protected by alternative physical safeguards.", 3, false),
        new("3.13.9", 13, "Terminate network connections associated with communications sessions at the end of the sessions or after a defined period of inactivity.", 1, false),
        new("3.13.10", 13, "Establish and manage cryptographic keys for cryptography employed in organizational systems.", 1, false),
        new("3.13.11", 13, "Employ FIPS-validated cryptography when used to protect the confidentiality of CUI.", 5, false),
        new("3.13.12", 13, "Prohibit remote activation of collaborative computing devices and provide indication of devices in use to users present at the device.", 1, false),
        new("3.13.13", 13, "Control and monitor the use of mobile code.", 1, false),
        new("3.13.14", 13, "Control and monitor the use of Voice over Internet Protocol (VoIP) technologies.", 1, false),
        new("3.13.15", 13, "Protect the authenticity of communications sessions.", 5, false),
        new("3.13.16", 13, "Protect the confidentiality of CUI at rest.", 1, false),
        new("3.14.1", 14, "Identify, report, and correct system flaws in a timely manner.", 5, true),
        new("3.14.2", 14, "Provide protection from malicious code at designated locations within organizational systems.", 5, true),
        new("3.14.3", 14, "Monitor system security alerts and advisories and take action in response.", 5, true),
        new("3.14.4", 14, "Update malicious code protection mechanisms when new releases are available.", 5, false),
        new("3.14.5", 14, "Perform periodic scans of organizational systems and real-time scans of files from external sources as files are downloaded, opened, or executed.", 3, false),
        new("3.14.6", 14, "Monitor organizational systems, including inbound and outbound communications traffic, to detect attacks and indicators of potential attacks.", 5, false),
        new("3.14.7", 14, "Identify unauthorized use of organizational systems.", 3, false),
    ];

    public static Nist171Requirement? Find(string id) => Requirements.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
}
