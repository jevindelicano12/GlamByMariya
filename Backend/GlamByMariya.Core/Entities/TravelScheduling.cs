namespace GlamByMariya.Core.Entities;

// Offline planning types only: these are not stored with, or enforced by, bookings.
public enum RouteEstimateStatus { Unavailable, InvalidRequest, Synthetic, Estimated }
public enum TravelReviewStatus { Unavailable, InvalidRequest, SyntheticConflict, SyntheticFit }
public enum RouteMode { Driving, Walking, Cycling }
public record RouteRequest(string OriginReference, string DestinationReference, DateTimeOffset? DepartAt,
    DateTimeOffset? ArriveBy, RouteMode Mode, TimeSpan TravelBuffer);
public record RouteEvidence(string Provider, string OriginReference, string DestinationReference,
    DateTimeOffset RequestedTime, bool ArrivalBased, RouteMode Mode, bool Synthetic);
public record RouteEstimate(RouteEstimateStatus Status, TimeSpan? Duration, RouteEvidence? Evidence);
public record TravelAnchor(string LocationReference, DateTimeOffset ReadyAt);
public record TravelStop(string LocationReference, DateTimeOffset MakeupStart, TimeSpan ServiceDuration, TimeSpan PreparationBuffer);
public record TravelProjection(string OriginReference, string DestinationReference, DateTimeOffset MakeupStart,
    DateTimeOffset Departure, DateTimeOffset MakeupEnd, TimeSpan ServiceDuration, TimeSpan PreparationBuffer,
    TimeSpan TravelDuration, TimeSpan TravelBuffer, RouteEstimateStatus EstimateStatus, RouteEvidence Evidence);
public record TravelReview(TravelReviewStatus Status, TravelProjection? Projection, RouteEstimate? NextLeg);
