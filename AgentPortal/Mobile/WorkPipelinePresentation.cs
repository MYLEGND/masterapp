namespace AgentPortal.Mobile;

// Read-only grouping of canonical states. These labels never authorize transitions.
public static class WorkPipelinePresentation
{
    public static string LeadBucket(string stage, bool archived = false) => archived || stage is "Lost" or "NotInterested" or "DoNotCallList" or "Closed"
        ? "closed" : stage is "PolicyPlaced" or "Client" or "Won" or "Completed" or "Complete" or "Finished" ? "completed" : "active";
    public static string OrderBucket(string fulfillment, string payment, string returns) =>
        fulfillment is "Cancelled" or "Returned" || payment == "Refunded" ? "closed" :
        returns is not ("None" or "Closed") ? "active" : fulfillment == "Fulfilled" ? "completed" : "active";
    public static string Tone(string stage) => stage switch {
        "Fulfilled" or "PolicyPlaced" or "Client" or "Won" or "Completed" or "Complete" or "Finished" => "success",
        "Cancelled" or "Returned" or "Refunded" or "Lost" or "NotInterested" or "DoNotCallList" or "Closed" => "danger",
        "Processing" or "Packed" or "Shipped" or "Contacted" or "Qualified" or "Booked" => "info",
        _ => "warning"
    };
}
