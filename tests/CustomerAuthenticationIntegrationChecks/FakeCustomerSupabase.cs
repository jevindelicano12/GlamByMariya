using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

internal sealed class FakeCustomerSupabase : IHttpClientFactory
{
    private sealed class Account(string email,string password)
    {
        internal Guid Id {get;set;}=Guid.NewGuid();
        internal string Email {get;}=email;
        internal string Password {get;set;}=password;
        internal bool Confirmed {get;set;}
    }
    private readonly Dictionary<string,Account> accounts=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,Account> tokens=[];
    private readonly object gate=new();
    internal int Signups {get;private set;}
    internal int Verifications {get;private set;}
    internal int Updates {get;private set;}
    internal bool TimeoutSignup {get;set;}
    internal bool TimeoutUpdate {get;set;}
    internal bool MismatchUser {get;set;}
    internal bool SignupHadBearer {get;private set;}
    internal Guid? ReuseSubject {get;set;}
    internal bool PauseNextPasswordLogin {get;set;}
    internal TaskCompletionSource LoginStarted {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ReleaseLogin {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Guid SubjectFor(string email) {lock(gate)return accounts[email].Id;}
    public HttpClient CreateClient(string name)=>new(new Handler(this));
    private object User(Account a,bool mismatch=false)=>new {
        id=mismatch ? Guid.NewGuid() : a.Id,email=a.Email,
        email_confirmed_at=a.Confirmed ? (DateTime?)DateTime.UtcNow : null,confirmation_sent_at=DateTime.UtcNow };
    private HttpResponseMessage Session(Account a)
    {
        var token="synthetic-"+Guid.NewGuid().ToString("N");
        tokens[token]=a;
        return new(HttpStatusCode.OK){Content=JsonContent.Create(new {access_token=token,refresh_token="synthetic",user=User(a)})};
    }
    private sealed class Handler(FakeCustomerSupabase owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if(request.RequestUri?.Host!="auth.example.test")throw new InvalidOperationException("External provider access refused.");
            var text=request.Content==null ? "{}" : await request.Content.ReadAsStringAsync(ct);
            using var body=JsonDocument.Parse(text);
            string Get(string name)=>body.RootElement.TryGetProperty(name,out var v) ? v.GetString() ?? "" : "";
            var path=request.RequestUri.AbsolutePath;
            Task? pause=null;
            lock(owner.gate)
            {
                if(path=="/auth/v1/token" && owner.PauseNextPasswordLogin)
                {
                    owner.PauseNextPasswordLogin=false;
                    owner.LoginStarted.TrySetResult();
                    pause=owner.ReleaseLogin.Task;
                }
            }
            if(pause!=null)await pause.WaitAsync(ct);
            lock(owner.gate)
            {
                var email=Get("email").Trim();
                if(path=="/auth/v1/signup")
                {
                    owner.SignupHadBearer=request.Headers.Authorization!=null;
                    owner.Signups++;
                    if(owner.accounts.ContainsKey(email))return new(HttpStatusCode.UnprocessableEntity){Content=JsonContent.Create(new {msg="already registered"})};
                    var a=new Account(email,Get("password"));
                    if(owner.ReuseSubject is {} subject)a.Id=subject;
                    owner.accounts[email]=a;
                    if(owner.TimeoutSignup)throw new TaskCanceledException("Synthetic uncertain signup.");
                    return new(HttpStatusCode.OK){Content=JsonContent.Create(owner.User(a))};
                }
                if(path is "/auth/v1/recover" or "/auth/v1/resend")return new(HttpStatusCode.OK){Content=JsonContent.Create(new {})};
                if(path=="/auth/v1/token" && owner.accounts.TryGetValue(email,out var login) && login.Confirmed && login.Password==Get("password"))
                    return owner.Session(login);
                if(path=="/auth/v1/verify")
                {
                    owner.Verifications++;
                    if(owner.accounts.TryGetValue(email,out var a) && Get("token")=="123456")
                    {a.Confirmed=true;return owner.Session(a);}
                }
                if(path=="/auth/v1/user" && request.Headers.Authorization?.Parameter is {} token && owner.tokens.TryGetValue(token,out var account))
                {
                    if(request.Method==HttpMethod.Put)
                    {
                        owner.Updates++;
                        account.Password=Get("password");
                        if(owner.TimeoutUpdate)throw new TaskCanceledException("Synthetic uncertain update.");
                    }
                    return new(HttpStatusCode.OK){Content=JsonContent.Create(owner.User(account,owner.MismatchUser))};
                }
                return new(HttpStatusCode.BadRequest){Content=JsonContent.Create(new {msg="Invalid login credentials"})};
            }
        }
    }
}
