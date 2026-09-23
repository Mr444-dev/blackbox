namespace Blackbox.Core.Domain;

public enum ReservationStatus
{
    Active,
    Released,
    Consumed
}

public class ResourceReservation
{
    public Guid Id { get; private set; }
    public string ResourceId { get; private set; }
    public Guid WorkflowId { get; private set; }
    public int Quantity { get; private set; }
    public ReservationStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private ResourceReservation()
    {
        ResourceId = string.Empty;
    }

    public ResourceReservation(string resourceId, Guid workflowId, int quantity)
    {
        Id = Guid.NewGuid();
        ResourceId = resourceId;
        WorkflowId = workflowId;
        Quantity = quantity;
        Status = ReservationStatus.Active;
        CreatedAt = DateTime.UtcNow;
    }

    public void Release()
    {
        if (Status != ReservationStatus.Active)
            throw new WorkflowInvariantException("ReservationStatus", $"Cannot release reservation in '{Status}' state.");
        Status = ReservationStatus.Released;
    }

    public void Consume()
    {
        if (Status != ReservationStatus.Active)
            throw new WorkflowInvariantException("ReservationStatus", $"Cannot consume reservation in '{Status}' state.");
        Status = ReservationStatus.Consumed;
    }
}

public class Resource
{
    public string Id { get; private set; }
    public string Name { get; private set; }
    public int AvailableQuantity { get; private set; }
    public int AllocatedQuantity { get; private set; }
    public int ConsumedQuantity { get; private set; }
    public int TotalCapacity => AvailableQuantity + AllocatedQuantity + ConsumedQuantity;
    public int Version { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private Resource()
    {
        Id = string.Empty;
        Name = string.Empty;
    }

    public Resource(string id, string name, int initialQuantity)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Resource ID cannot be empty", nameof(id));
        if (initialQuantity < 0)
            throw new ArgumentException("Initial quantity cannot be negative", nameof(initialQuantity));

        Id = id;
        Name = name;
        AvailableQuantity = initialQuantity;
        AllocatedQuantity = 0;
        ConsumedQuantity = 0;
        Version = 1;
        UpdatedAt = DateTime.UtcNow;
    }

    public ResourceReservation Reserve(Guid workflowId, int quantity)
    {
        if (quantity <= 0)
            throw new WorkflowInvariantException("InvalidQuantity", "Reservation quantity must be strictly greater than zero.");

        if (AvailableQuantity < quantity)
        {
            throw new WorkflowInvariantException(
                "InsufficientResourceCapacity",
                $"Cannot reserve {quantity} units of '{Id}'. Available: {AvailableQuantity}, Allocated: {AllocatedQuantity}.");
        }

        AvailableQuantity -= quantity;
        AllocatedQuantity += quantity;
        Version++;
        UpdatedAt = DateTime.UtcNow;

        return new ResourceReservation(Id, workflowId, quantity);
    }

    public void ReleaseReservation(ResourceReservation reservation)
    {
        if (reservation.ResourceId != Id)
            throw new WorkflowInvariantException("ResourceMismatch", "Reservation does not belong to this resource.");

        reservation.Release();
        AvailableQuantity += reservation.Quantity;
        AllocatedQuantity -= reservation.Quantity;
        Version++;
        UpdatedAt = DateTime.UtcNow;
    }

    public void ConsumeReservation(ResourceReservation reservation)
    {
        if (reservation.ResourceId != Id)
            throw new WorkflowInvariantException("ResourceMismatch", "Reservation does not belong to this resource.");

        reservation.Consume();
        AllocatedQuantity -= reservation.Quantity;
        ConsumedQuantity += reservation.Quantity;
        Version++;
        UpdatedAt = DateTime.UtcNow;
    }
}
