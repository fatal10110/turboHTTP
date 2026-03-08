## 19b.1: Unify Pipeline (Remove Interceptors)

**Goal:** Eliminate the overlap between the Pipeline and Interceptor architectures by removing the latter.

Summary:
1. Delete `IHttpInterceptor`, `InterceptorRequestResult`, `InterceptorResponseResult`, and related structs.
2. Remove interception invocation loops (`ExecuteWithInterceptorsAsync`) from `UHttpClient`.
3. Remove `UHttpClientOptions.Interceptors` and `InterceptorFailurePolicy`.
4. Update the Plugin infrastructure (`IHttpPlugin`) to contribute `IHttpMiddleware` instances instead of interceptors.
