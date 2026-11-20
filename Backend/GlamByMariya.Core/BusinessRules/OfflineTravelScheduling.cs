using GlamByMariya.Core.Entities;

namespace GlamByMariya.Core.BusinessRules;

public interface ITravelEstimator
{
    ValueTask<RouteEstimate> Estimate(RouteRequest request, CancellationToken cancellationToken = default);
}

// Only exact synthetic identifiers work; no URLs, addresses, network or credentials.
public sealed class SyntheticTravelEstimator(IReadOnlyDictionary<(string Origin, string Destination), TimeSpan> routes) : ITravelEstimator
{
    public ValueTask<RouteEstimate> Estimate(RouteRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request == null || string.IsNullOrEmpty(request.OriginReference) || string.IsNullOrEmpty(request.DestinationReference) ||
            !request.OriginReference.StartsWith("SYNTHETIC:", StringComparison.Ordinal) || !request.DestinationReference.StartsWith("SYNTHETIC:", StringComparison.Ordinal) ||
            request.DepartAt.HasValue == request.ArriveBy.HasValue || !Enum.IsDefined(request.Mode) || request.TravelBuffer < TimeSpan.Zero || request.TravelBuffer > TimeSpan.FromDays(1))
            return ValueTask.FromResult(new RouteEstimate(RouteEstimateStatus.InvalidRequest, null, null));
        if (routes == null || !routes.TryGetValue((request.OriginReference, request.DestinationReference), out var duration) || duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(7))
            return ValueTask.FromResult(new RouteEstimate(RouteEstimateStatus.Unavailable, null, null));
        var evidence = new RouteEvidence("synthetic-only", request.OriginReference, request.DestinationReference,
            (request.ArriveBy ?? request.DepartAt)!.Value, request.ArriveBy.HasValue, request.Mode, true);
        return ValueTask.FromResult(new RouteEstimate(RouteEstimateStatus.Synthetic, duration, evidence));
    }
}

// Not registered with MVC or used for reservation decisions. A synthetic fit is never a real route confirmation.
public sealed class OfflineTravelScheduler(ITravelEstimator estimator)
{
    public async ValueTask<TravelReview> Review(TravelAnchor? previous, TravelStop candidate, TravelStop? next,
        bool nextKnown, RouteMode mode, TimeSpan travelBuffer, CancellationToken cancellationToken = default)
    {
        if (previous == null || candidate == null || !nextKnown || string.IsNullOrWhiteSpace(previous.LocationReference) ||
            !ValidStop(candidate) || (next != null && !ValidStop(next)) || !Enum.IsDefined(mode) ||
            travelBuffer < TimeSpan.Zero || travelBuffer > TimeSpan.FromDays(1))
            return new(TravelReviewStatus.InvalidRequest, null, null);
        try
        {
            var inboundRequest = new RouteRequest(previous.LocationReference, candidate.LocationReference, null, candidate.MakeupStart, mode, travelBuffer);
            var inbound = await estimator.Estimate(inboundRequest, cancellationToken);
            if (!Matches(inbound, inboundRequest)) return new(TravelReviewStatus.Unavailable, null, null);
            var departure = candidate.MakeupStart - inbound.Duration!.Value - travelBuffer;
            var end = candidate.MakeupStart + candidate.ServiceDuration;
            var projection = new TravelProjection(previous.LocationReference, candidate.LocationReference, candidate.MakeupStart,
                departure, end, candidate.ServiceDuration, candidate.PreparationBuffer, inbound.Duration.Value, travelBuffer, inbound.Status, inbound.Evidence!);
            if (previous.ReadyAt > departure) return new(TravelReviewStatus.SyntheticConflict, projection, null);
            if (next == null) return new(TravelReviewStatus.SyntheticFit, projection, null);
            var onwardRequest = new RouteRequest(candidate.LocationReference, next.LocationReference, null, next.MakeupStart, mode, travelBuffer);
            var onward = await estimator.Estimate(onwardRequest, cancellationToken);
            if (!Matches(onward, onwardRequest)) return new(TravelReviewStatus.Unavailable, projection, onward);
            var nextDeparture = next.MakeupStart - onward.Duration!.Value - travelBuffer;
            return new(end + candidate.PreparationBuffer <= nextDeparture ? TravelReviewStatus.SyntheticFit : TravelReviewStatus.SyntheticConflict, projection, onward);
        }
        catch (ArgumentOutOfRangeException) { return new(TravelReviewStatus.InvalidRequest, null, null); }
        catch (Exception e) when (e is not OperationCanceledException) { return new(TravelReviewStatus.Unavailable, null, null); }
    }

    private static bool ValidStop(TravelStop stop) => !string.IsNullOrWhiteSpace(stop.LocationReference) &&
        stop.ServiceDuration > TimeSpan.Zero && stop.ServiceDuration <= TimeSpan.FromDays(1) &&
        stop.PreparationBuffer >= TimeSpan.Zero && stop.PreparationBuffer <= TimeSpan.FromDays(1);
    private static bool Matches(RouteEstimate? estimate, RouteRequest request) => estimate?.Status == RouteEstimateStatus.Synthetic &&
        estimate.Duration is { } duration && duration > TimeSpan.Zero && duration <= TimeSpan.FromDays(7) &&
        estimate.Evidence is { Synthetic: true } evidence && evidence.Provider == "synthetic-only" &&
        evidence.OriginReference == request.OriginReference && evidence.DestinationReference == request.DestinationReference &&
        evidence.ArrivalBased && evidence.RequestedTime == request.ArriveBy && evidence.Mode == request.Mode;
}
