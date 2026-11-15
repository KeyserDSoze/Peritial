namespace Peritial.Domains;

public enum PeriziaStatus
{
    Registered = -1,
    Assigned = 0,
    ToBeRevised = 1,
    Revised = 2,
    ToSend = 3,
    Sent = 4,
    Billed = 5,
    Duplicated = 6,
    Cancelled = 7
}
